# Clipboard Sync — Protocol (v1)

> 이 문서는 **구현의 단일 출처**다. 근거와 배경은 `docs/01-tech-spec.md`(§3, §4)와 결정 로그(D-2~D-5, D-25, D-30~D-33).
> 모든 구현(TypeScript 참조 `protocol/ref`, Swift `mac/ClipSyncCore`, C# `windows/ClipSync.Core`)은 `protocol/test-vectors.json`을 읽는 테스트로 검증한다.
> 규칙이 모호하면 **코드를 고치기 전에 이 문서와 spec을 먼저 고친다.**

## 1. 표기
- 정수는 모두 **big-endian**. `u8/u16/u32/u64`.
- `hex()`는 **소문자**, 구분자 없음.
- 문자열은 UTF-8 바이트로 다룬다. 길이는 항상 **UTF-8 바이트 수**.

## 2. 키 파생
```
pass_norm   = collapse_ws(NFKD(passphrase))               # 2.1
master      = PBKDF2-HMAC-SHA256(UTF8(pass_norm), UTF8("clipsync/v1/salt"), 600000, 32B)
enc_key     = HKDF-SHA256(ikm=master, salt=<empty>, info=UTF8("clipsync/v1/enc"),  L=32)
auth_token  = HKDF-SHA256(ikm=master, salt=<empty>, info=UTF8("clipsync/v1/auth"), L=32)
vault_id    = hex(SHA-256(auth_token))
```
- PBKDF2에는 **바이트**를 넘긴다(문자열 오버로드 금지). HKDF salt는 빈 값(=0x00 × 32, RFC 5869). `info`는 NUL 없는 UTF-8 원문.
- 저장하는 것은 `enc_key`, `auth_token`뿐이다.

### 2.1 passphrase 정규화 (D-32)
1. Unicode **NFKD** 정규화.
2. 공백 집합 `{U+0009–U+000D, U+0020, U+0085, U+00A0, U+1680, U+2000–U+200A, U+2028, U+2029, U+202F, U+205F, U+3000}`의 **연속을 U+0020 하나로** 치환.
3. 양끝의 U+0020 제거.
4. 대소문자는 바꾸지 않는다.
- 언어 내장 공백 판정(`char.IsWhiteSpace`, JS `\s`, Swift `.whitespaces`)은 집합이 달라 **사용하지 않고** 위 목록을 그대로 구현한다.

## 3. 식별자 (D-33)
- `item_id`, `device_id`: UUID v4.
- **와이어/AAD/마커 표현: RFC 4122 순서의 16바이트** (문자열 `f47ac10b-58cc-4372-a567-0e02b2c3d479`를 왼쪽부터 hex로 읽은 바이트열 `f47ac10b58cc4372a5670e02b2c3d479`).
- 문자열·URL·R2 키 표현: 소문자 hex 32자(하이픈 없음). R2 키는 서버 내부 규칙(spec D-34)이며 클라이언트와 무관하다.
- C#: `Guid.ToByteArray()`는 혼합 엔디언이므로 그대로 쓰지 않는다 (`ToByteArray(bigEndian: true)` 또는 직접 생성).

## 4. 암호화 단위
- AES-256-GCM, tag 16B, nonce 12B(파트마다 CSPRNG). 저장 형식 `nonce(12) || ciphertext || tag(16)`.
- 항목은 header/body 두 개의 독립 암호문, vault 설정(config)은 별도 암호문.
- **AAD**
  - 항목: `schema_version(u8=1) || item_id(16) || origin_device_id(16) || created_at(u64, unix ms) || part(u8: 1=header, 2=body)`
  - 설정: `schema_version(u8=1) || config_version(u64) || part(u8=3)`
    - `config_version`은 **이 blob이 서버에 저장된 뒤 갖게 될 version**이다. `PUT /v1/config`의 `If-Match: N`이면 `N+1`로 봉인하고, 받는 쪽은 서버가 알려 준 `version`으로 연다 (spec D-50).
- 공개 `seal`은 nonce를 받지 않는다. **고정 nonce는 테스트 전용 진입점**(`sealWithNonce`)으로만 주입한다.
- `open` 실패(태그/AAD/키 불일치, 길이 < 28바이트)는 예외/오류이며 평문을 반환하지 않는다.

## 5. header / config (JSON)
- header, config 평문은 UTF-8 JSON이다 (필드는 spec §4.3, §4.4).
- **JSON은 정규형이 아니다** (키 순서, `/`·비ASCII 이스케이프가 언어마다 다름). 그래서 벡터는 **고정된 평문 바이트 → 암호문**을 담고, 테스트는 복호화 후 **파싱한 필드**를 비교한다. 인코더 출력 바이트를 언어 간에 비교하지 않는다.
- header `preview`: **먼저 NFC 정규화한 뒤** 앞 200 **code point**(UTF-16 단위 아님)를 자른다. 이모지가 경계에 걸려도 서로게이트를 쪼개지 않는다. NFD 한글은 정규화 전에 세면 자르는 위치가 달라지므로 순서를 지킨다. (`previewOf`)

## 6. 번들 (body 평문, 바이트 비교 대상)
```
magic "CSB1"(4B) | version u8 (=1) | count u16
entry × count:  type u8 | name_len u16 | name (UTF-8) | data_len u32 | data
type: 1=text/plain(UTF-8)  2=text/html(UTF-8 fragment)  3=image/png  4=file
```
- **인코딩 순서(정규):** type 오름차순(1,2,3,4). 같은 type의 여러 엔트리(파일)는 입력 순서를 유지한다. 인코더는 입력을 이 순서로 정렬한다.
- `name_len = 0` (type 1–3). type 4는 파일명(NFC, sanitize 전 원본 아님 — 송신 측 NFC 정규화, 수신 측 sanitize).
- 디코더 규칙: **알 수 없는 type은 건너뛴다**(`data_len`로 스킵). 아래는 **오류**: magic 불일치, `version != 1`, 헤더/엔트리 잘림, `count`와 실제 엔트리 수 불일치, 마지막 엔트리 뒤의 여분 바이트, 알려진 type 1–3에서 `name_len != 0`, **type 4 파일명이 유효하지 않은 UTF-8**(치환 문자로 조용히 바꾸지 않는다).
- 테스트는 **encode→bytes**와 **bytes→decode** 양방향을 검증한다.
- 디렉터리 엔트리는 없다 — 폴더가 포함된 복사는 송신 단계에서 항목 전체가 무시된다 (D-31).

## 7. 테스트 벡터 (`protocol/test-vectors.json`)
| 섹션 | 내용 |
|---|---|
| `passphrases` | passphrase(원문) → `pass_norm` 바이트, master, enc_key, auth_token, vault_id. 공백/유니코드 변형은 같은 키로 수렴해야 한다 |
| `uuids` | UUID **문자열** → 16바이트 → hex id (Guid 혼합 엔디언 버그 검출) |
| `seals` | 고정 nonce로 만든 header/body/config 암호문. 입력은 UUID 문자열·정수 필드·평문 바이트, 기대값은 AAD 바이트와 sealed 바이트. 테스트는 (1) AAD를 필드에서 **재구성**해 일치 확인, (2) `sealWithNonce` 결과 일치, (3) 복호화 성공 |
| `negatives` | `expect: "fail"`. header/body 파트 바꿔치기, 다른 item_id의 AAD, 잘못된 키, 잘린 암호문, 짧은 입력, 태그/AAD 변조 |
| `bundles` | 엔트리 목록 ↔ 바이트. 인코드/디코드 양방향 |
| `bundle_invalid` | `expect: "error"`인 잘못된 바이트열 |
| `previews` | `preview` 입력 → 기대 출력 (code point 경계, NFD→NFC) |
- 벡터는 TypeScript 참조 구현(`protocol/ref`)이 생성한다. 파일은 **재생성 결과와 항상 같아야** 하며(참조 테스트가 검사), 값 변경은 스펙 변경이다.
