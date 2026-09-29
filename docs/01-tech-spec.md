# Clipboard Sync — 기술 명세서 (Tech Spec)

> 상태: 설계 확정 (구현 미착수)
> 작성일: 2026-09-29 / 개정: 2026-09-29 (R2 대용량 본문, 수신 후 삭제, vault 설정), 2026-09-29 (advisor 리뷰 반영: 서명·로컬 우선 규칙·RDP 에코·NFC·소스 포맷 변환·.NET 10)
> 선행 문서: [00-requirements.md](./00-requirements.md) (FR-*, NFR-* 번호는 이 문서를 참조)

## 1. 아키텍처 개요

```
┌────────────────┐   HTTPS (본문 PUT/GET/DELETE, 목록)   ┌──────────────────────────────┐
│ macOS 앱 (Swift)│ ────────────────────────────────────▶ │ Cloudflare Worker            │
│  메뉴바 / 감시  │ ◀── WebSocket (이벤트) ────────────── │  - 인증(allowlist), rate limit│
│  + 설정 편집   │                                       │  - 본문 스트리밍 → R2        │
└────────────────┘                                       └───────┬───────────┬──────────┘
┌────────────────┐                                  idFromName   │           │ put/get/delete
│Windows 앱(.NET)│ ◀───────────── 동일 ──────────────────────────▼           ▼
│  트레이 / 감시  │                                  ┌───────────────────┐  ┌────────────┐
│  (설정 읽기전용)│                                  │ Durable Object     │  │ R2 bucket  │
└────────────────┘                                  │ "Vault"            │  │ (>1MiB 본문)│
                                                     │  SQLite: items,    │  └────────────┘
                                                     │  bodies, config    │
                                                     │  WS Hibernation    │
                                                     │  Alarm(만료/청소)  │
                                                     └───────────────────┘
```

- 서버는 **암호문만** 다룬다 (NFR-1). 복호화는 클라이언트에서만 수행한다.
- 사용자(vault) 1명당 Durable Object 1개. 단일 사용자이므로 서버 전체에 vault는 사실상 1개다.
- WebSocket은 이벤트 통지 전용이고, 본문은 HTTP로 전송한다 (Section 5).
- **DO에는 큰 본문을 두지 않는다.** 1.25 MiB 이하 본문만 DO SQLite에, 그 이상은 Worker가 R2로 스트리밍한다 (DO 행 한도 2MB, DO 메모리 제약).

## 2. 저장소 구조 (모노레포)

```
clipboard-sync/
├─ docs/                  # 00-requirements, 01-tech-spec, ...
├─ protocol/
│   ├─ PROTOCOL.md        # 바이트 포맷/암호 파라미터의 단일 출처
│   ├─ test-vectors.json  # passphrase→키→암호문, 번들/설정 인코딩 벡터 (Swift/C# 테스트 공용)
│   └─ ref/               # TypeScript 참조 구현: 벡터 생성기 + CLI 테스트 피어 (서버·앱 상호 검증)
├─ server/                # Cloudflare Worker + DO (TypeScript, wrangler)
├─ mac/                   # Swift 앱 (Xcode project + Swift Package)
└─ windows/               # .NET 10 솔루션
```

## 3. 암호 설계 (NFR-1, FR-7)

### 3.1 알고리즘 선택
| 용도 | 선택 | 이유 |
|---|---|---|
| passphrase 확장 | **PBKDF2-HMAC-SHA256, 600,000회** | CommonCrypto(`CCKeyDerivationPBKDF`)와 .NET(`Rfc2898DeriveBytes.Pbkdf2`) 모두 OS 내장 → 추가 의존성 0 |
| 키 분리 | HKDF-SHA256 | 용도별 키 분리 |
| 암호화 | **AES-256-GCM** | CryptoKit `AES.GCM`, .NET `AesGcm` 내장. 최대 50 MiB 본문은 one-shot 암호화 (메모리 내) |

Argon2id는 두 플랫폼 표준 라이브러리에 없어 libsodium 바인딩 의존성이 생긴다. 개인용 2-디바이스 도구에서는 **고엔트로피 passphrase + PBKDF2**로 충분하다고 판단했다 (D-2).

### 3.2 키 파생
```
salt        = UTF8("clipsync/v1/salt")                 # 고정 상수 (서버 왕복 없이 파생해야 하므로)
master      = PBKDF2(passphrase_NFKD_utf8, salt, 600000, 32B)
enc_key     = HKDF-SHA256(ikm=master, salt=empty, info=UTF8("clipsync/v1/enc"),  L=32)
auth_token  = HKDF-SHA256(ikm=master, salt=empty, info=UTF8("clipsync/v1/auth"), L=32)
vault_id    = lowercase_hex(SHA-256(auth_token))
```
- passphrase는 **NFKD 정규화 → 공백 정규화 → UTF-8 바이트**로 만든 뒤 PBKDF2에 바이트로 전달한다(문자열 오버로드 사용 금지). **공백 정규화(D-32):** NFKD 뒤에 공백 집합 `{U+0009–U+000D, U+0020, U+0085, U+00A0, U+1680, U+2000–U+200A, U+2028, U+2029, U+202F, U+205F, U+3000}`의 연속을 U+0020 하나로 바꾸고 양끝을 제거한다. 대소문자는 바꾸지 않는다. 언어별 내장 공백 판정(`char.IsWhiteSpace`, JS `\s` 등)은 집합이 달라 쓰지 않고 이 목록을 그대로 구현한다.
- HKDF salt는 **빈 값**(RFC 5869: 해시 길이만큼의 0바이트와 동일). PBKDF2가 이미 salt를 적용했으므로 추가 salt는 두지 않는다. `info`는 라벨의 UTF-8 원문(NUL 종료 없음). (D-30)
- 고정 salt는 **passphrase가 고엔트로피일 때만 안전**하다. 첫 기기의 앱이 **passphrase를 생성**한다: EFF long wordlist 7단어 (약 90bit). 사용자는 두 번째 기기에 그대로 입력한다.
- 직접 정한 passphrase는 24자 이상 + 경고 문구를 조건으로 허용한다.
- `info` 라벨에 버전(`v1`)을 포함해 파라미터 변경 시 키가 자동 분리되게 한다.
- Keychain / Credential Manager에는 **`enc_key`, `auth_token`만 저장**한다 (passphrase 원문 저장 안 함).

### 3.3 암호화 단위
- 항목(item)은 **header**와 **body** 두 개의 독립 AES-GCM 암호문. vault 설정(config)은 별도 암호문.
- Nonce: 파트마다 CSPRNG 96bit 랜덤. 저장 형식은 `nonce(12) || ciphertext || tag(16)`.
- **AAD**
  - 항목: `schema_version(u8) || item_id(16B) || origin_device_id(16B) || created_at(u64 BE, unix ms) || part(u8: 1=header, 2=body)`
  - 설정: `schema_version(u8) || config_version(u64 BE) || part(u8: 3)`
- 서버가 조작한 메타데이터(항목 id 교체, 다른 항목의 body 바꿔치기 등)는 복호화 시 인증 실패로 드러난다.

## 4. 데이터 모델 / 번들 포맷

### 4.1 식별자
- `item_id`: 클라이언트가 만드는 UUID v4 (16B). 에코 방지 마커와 AAD에도 쓴다. **바이트 표현(D-33):** 와이어/AAD/마커는 RFC 4122 순서 16바이트(문자열 `xxxxxxxx-xxxx-…`를 왼쪽부터 hex로 읽은 순서). 문자열·URL·R2 키는 소문자 hex. C# `Guid.ToByteArray()`의 혼합 엔디언 순서를 그대로 쓰지 않는다(`bigEndian: true` 오버로드 또는 직접 생성).
- `device_id`: 기기 최초 실행 시 생성되는 UUID v4.
- `seq`: **서버(DO)가 커밋 시점에 부여하는 단조 증가 정수**. 항목 순서와 "최신"의 정의는 오직 seq (클라이언트 시계 사용 금지). `created_at`은 표시용이며 AAD에 포함된다.

### 4.2 서버 저장 스키마 (DO SQLite + R2)
```sql
CREATE TABLE items (
  seq         INTEGER PRIMARY KEY AUTOINCREMENT,
  id          BLOB NOT NULL UNIQUE,     -- 16B item_id
  device_id   BLOB NOT NULL,            -- 16B
  created_at  INTEGER NOT NULL,         -- unix ms (클라이언트 값)
  expires_at  INTEGER NOT NULL,         -- 서버 시각 + 24h
  header      BLOB NOT NULL,            -- 암호화된 header
  body_size   INTEGER NOT NULL,         -- 암호화된 body 바이트 수
  purged      INTEGER NOT NULL DEFAULT 0  -- 1 = 본문 삭제됨 (수신 후 삭제)
);
-- 마지막으로 부여된 seq는 AUTOINCREMENT의 sqlite_sequence로 얻는다 (행이 지워져도 줄지 않음, D-36).
CREATE TABLE bodies (
  id          BLOB PRIMARY KEY,         -- item_id (PUT 시점에 생성, 커밋 전에는 items 행 없음)
  storage     TEXT NOT NULL,            -- 'do' | 'r2'
  size        INTEGER NOT NULL,
  body        BLOB,                     -- storage='do'일 때만 (BLOB, base64 아님)
  r2_key      TEXT,                     -- storage='r2'일 때만. 업로드마다 고유 (D-34)
  created_at  INTEGER NOT NULL,
  committed   INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE config (
  id      INTEGER PRIMARY KEY CHECK (id = 1),
  version INTEGER NOT NULL,             -- PUT마다 +1
  blob    BLOB NOT NULL                 -- 암호화된 vault 설정
);
```
- R2 객체 키: `<vault_id>/<hex(item_id)>/<업로드 nonce 16B hex>` (D-34). `bodies.r2_key`에 기록. 삭제는 항상 DO가 R2 binding으로 수행한다 (수신 후 삭제, 20개 cap 초과분, 만료, 고아 정리).
- **서버가 볼 수 있는 정보**: 항목 id, 기기 id, 시각, 암호문 크기, seq, purged 여부. **타입·파일명·미리보기·설정 값은 암호문 안에만** 존재한다 (서버는 타입을 알지 못한다).

### 4.3 header / body 분리
- **header (평문 JSON, UTF-8, 암호화 후 저장)**: 목록 표시용. 수 KB 이하.
  ```json
  { "v": 1,
    "kinds": ["text","html","image","files"],
    "preview": "앞 200자 (text/html일 때)",
    "image": { "w": 1920, "h": 1080 },
    "files": [ { "name": "a.pdf", "size": 12345 } ],
    "body_plain_size": 123456 }
  ```
- **body (평문 바이너리, 암호화 후 저장)**: 실제 콘텐츠. 항목을 적용하거나 목록에서 클릭할 때만 내려받는다.
  ```
  magic "CSB1"(4B) | version u8 | count u16
  entry × count: type u8 | name_len u16 | name (UTF-8) | data_len u32 | data
  type: 1=text/plain(UTF-8)  2=text/html(UTF-8)  3=image/png  4=file
  ```
  - 정수는 모두 big-endian. 압축은 v1에서 사용하지 않는다 (D-9).
  - 가능한 경우 text/plain fallback을 포함한다 (단, 파일 항목에는 파일명 텍스트를 넣지 않는다 — 4.5 참조). HTML은 fragment만 저장하고 Windows `CF_HTML` 헤더는 수신 측이 생성한다.
  - 이미지의 정규 포맷은 **PNG**. 소스 앱이 PNG를 주지 않으면 송신 측이 변환한다: macOS는 TIFF → PNG, Windows는 `CF_DIBV5`/`CF_DIB` → PNG (알파 보존).
  - **RTF → HTML 변환 (macOS 송신)**: TextEdit/Notes/Pages/Word 등은 HTML 없이 RTF만 pasteboard에 넣는 경우가 많다. HTML 표현이 없고 RTF가 있으면 `NSAttributedString`으로 읽어 HTML fragment로 변환해 전송한다 (plain text fallback은 그대로 포함). Windows 송신은 `HTML Format`이 있으면 사용하고 RTF는 v1에서 변환하지 않는다.
  - **유니코드 정규화**: 파일 이름과 header의 preview 문자열은 송신 시 **NFC로 정규화**한다. Finder는 한글 파일명을 자모 분리(NFD)로 넘기는 경우가 있어, 그대로 보내면 Windows에서 자모가 분리되어 보인다. 본문 텍스트 자체는 원문을 유지한다 (사용자가 복사한 텍스트를 바꾸지 않는다).
  - 파일 이름은 수신 시 sanitize 한다 (경로 구분자 제거, Windows 예약 문자/이름 `CON`, `NUL` 등 치환, 길이 제한).

### 4.4 vault 설정 (config)
```json
{ "v": 1, "images": false, "files": false, "max_media_bytes": 20971520 }
```
- 기본값(설정이 없거나 아직 못 받은 경우): **텍스트만** (`images=false, files=false`). 즉 **fail-closed**.
- `max_media_bytes` 허용값: 5 / 10 / 20(기본) / 50 MiB. 텍스트 전용 항목의 한도는 상수 1 MiB.
- 서버는 이 값을 읽을 수 없다. 따라서 **설정 강제는 송신 측 클라이언트**가 한다. 서버는 별도의 **상수 하드 캡**만 적용한다 (5.1).
- 클라이언트는 본 것 중 **가장 높은 config_version**만 수용한다 (오래된 blob 재전송을 무시; 서버의 롤백 공격은 완전히 막지 못하며 개인용 위협모델에서 수용).

### 4.5 타입 판별 / 크기 규칙 (FR-2, FR-3)
송신 측이 클립보드의 표현들을 다음 순서로 판별한다.
1. **파일 URL/HDROP이 있으면 파일 항목.** `files=false`이면 항목 전체 무시 (Finder/Explorer가 함께 넣는 파일명 문자열로 폴백하지 않는다). `files=true`이면 body에 파일만 담는다. **경로 중 디렉터리가 하나라도 있으면 항목 전체를 무시하고 사용자에게 알린다 (D-31; v1 번들에 디렉터리 엔트리 타입이 없다).**
2. 그 외 이미지 표현이 있으면: `images=false`이면 이미지 표현을 버리고 text/html 표현이 남으면 그것만 전송, 남는 게 없으면 무시. `images=true`이면 이미지(+남은 텍스트 표현)를 전송.
3. 텍스트/HTML만 있으면 텍스트 항목.

크기: body에 image/file 엔트리가 있으면 `max_media_bytes` (평문 합계) 이하, 없으면 1 MiB 이하. 초과 시 업로드하지 않고 알림 (타입 off로 무시된 경우는 조용히 무시).

서버 하드 캡: body 암호문 **51 MiB** 초과 시 413 (상수; 사용자 설정값과 무관).

## 5. 서버 API (Cloudflare Worker)

### 5.1 인증 / 남용 방지 (D-5)
- 클라이언트는 모든 요청에 `Authorization: Bearer <base64url(auth_token)>` 를 보낸다 (WebSocket upgrade 포함). **base64url은 패딩 없음, 엄격 디코드, 정확히 32바이트여야 하며 아니면 401** (D-35).
- Worker는 `SHA-256(auth_token)` 이 wrangler secret **`VAULT_ID`** 와 (상수시간 비교로) 일치하는지 확인한다. **불일치 시 DO/R2를 건드리지 않고 401**. 401은 IP 기준 rate limit (Workers Rate Limiting binding).
- 본문 업로드는 `Content-Length` 필수(없으면 411), 51 MiB 초과는 인증 통과 후에도 즉시 413 (본문을 읽기 전에 거부).
- 최초 설정: 첫 기기가 passphrase를 생성하면 앱이 `vault_id`를 표시 → 사용자가 `wrangler secret put VAULT_ID` 로 등록. 이후 기기는 passphrase와 서버 URL만 입력.
- Workers 요청 본문 한도는 플랜에 따라 100MB 이상이라 51 MiB 업로드에 충분하다 (조사 확인).

### 5.2 엔드포인트
| Method / Path | 설명 |
|---|---|
| `GET /v1/ws?device_id=` | WebSocket upgrade. 연결 직후 서버가 `hello` 전송. catch-up은 `GET /v1/items?since=`로 한다 (`last_seq` 파라미터 없음, D-36) |
| `PUT /v1/items/{id}/body` | **1단계: 본문 업로드.** 헤더: `X-Device-Id`, `Content-Length`. 본문(binary)은 body 암호문. ≤1.25 MiB는 DO(`bodies`)에, 초과는 Worker가 **R2로 스트리밍** (`FixedLengthStream`), DO에는 메타만 기록. 커밋 전 같은 id 재PUT은 덮어쓰기 (재시도 안전). **이미 커밋되었거나 purged된 id는 409**. R2 경로는 Worker가 스트리밍 전 DO `begin`(409 사전 검사, 업로드 nonce 발급)과 후 `finish`(트랜잭션 재검사 후 행 교체, 이전 객체 삭제; 그 사이 커밋/삭제되었으면 방금 쓴 객체를 지우고 409)를 호출한다 (D-34) (DELETE 뒤에 재시도된 PUT이 삭제된 본문을 되살리는 것을 방지) |
| `POST /v1/items` | **2단계: 커밋.** 헤더: `X-Item-Id`, `X-Device-Id`, `X-Created-At`. 본문(binary)은 header 암호문. 해당 id의 본문이 있어야 하며(없으면 409), seq 부여 후 broadcast. 응답 `{seq}`. 같은 id 재커밋은 기존 seq 반환 (idempotent). header 암호문은 64 KiB 초과 시 413 |
| `GET /v1/items?since=<seq>` | seq 초과 항목 목록 (최대 20개, 만료 제외, purged 여부 포함). 원소: `seq, id(hex), device_id(hex), created_at, header(base64), body_size, purged` |
| `GET /v1/items/{id}/body` | body 암호문 (스트리밍). 404=없음/만료, **410=purged(수신 후 삭제됨)** |
| `DELETE /v1/items/{id}/body` | **수신 기기가 클립보드 적용 후 호출** (idempotent; 커밋되지 않았거나 없는 id는 404). 본문(DO 행 또는 R2 객체) 삭제, `purged=1`, `body_purged` 이벤트 broadcast. header는 유지 |
| `GET /v1/config` | `{version, blob(base64)}`; 설정이 없으면 404 (= 텍스트만) |
| `PUT /v1/config` | 헤더 `If-Match: <version>`(최초는 0), 본문(binary)은 암호화된 config blob(`Content-Length` 필수, 1B~64 KiB). 응답 `{version}`. 낙관적 동시성; 성공 시 version+1, `config` 이벤트 broadcast. 버전 불일치는 **412** + 본문 `{version}`. blob은 64 KiB 초과 시 413. UI는 Mac만 노출하지만 API는 동일 토큰으로 동작 |

- 업로드 순서(PUT→POST)는 커밋된 항목만 이벤트로 보이게 하고, 대용량 스트림을 DO 메모리에 올리지 않기 위한 것이다 (D-15, D-20).
- 클라이언트는 대용량 업로드/다운로드 동안 진행률을 표시하고 긴 타임아웃(예: 5분)을 사용한다.

### 5.3 WebSocket 메시지 (JSON)
- 서버→클라이언트
  - `{"t":"hello","seq":<마지막으로 부여된 seq (D-36; 항목이 없으면 0)>,"config":{"version":N,"blob":"<b64>"}|null}`
  - `{"t":"item","seq","id","device_id","created_at","header":"<b64>","body_size","inline_body":"<b64>|null"}` — body 암호문이 32KB 이하면 inline (2초 목표, NFR-2). 업로드한 기기의 소켓에는 보내지 않는다.
  - `{"t":"body_purged","id":"<hex>"}` — 다른 기기가 적용을 마쳐 본문이 삭제됨. 클라이언트는 해당 항목을 "서버에서 삭제됨"으로 표시 (로컬 캐시가 있으면 복원 가능).
  - `{"t":"config","version":N,"blob":"<b64>"}`
- **인코딩 (D-35):** `id`/`device_id`는 소문자 hex(헤더 `X-Item-Id`/`X-Device-Id` 포함), `header`/`inline_body`/config `blob`은 표준 base64(패딩 있음, `+/`). Bearer 토큰만 base64url.
- 클라이언트→서버: 없음. Heartbeat는 DO **auto-response**(`"ping"`→`"pong"`)로 처리해 DO를 깨우지 않는다. 클라이언트는 **30초마다** `"ping"`을 보내고, **90초간** `"pong"`이 없으면 재연결한다.

### 5.4 Durable Object 동작
- SQLite-backed DO + **WebSocket Hibernation API**. R2 binding 보유.
- 커밋 시 `seq` 부여 → 20개 초과분의 항목·본문(DO 행/R2 객체) 즉시 삭제 (FR-5) → 다른 소켓에 `item` 이벤트 broadcast.
- 만료(24h): DO **alarm**으로 만료 항목의 **행과 본문을 모두 삭제**(이후 GET은 404), 읽기 시에도 `expires_at > now` 필터. purged 항목도 20개 cap에 포함한다 (D-37).
- alarm은 하나만 쓰며 `min(다음 만료, 가장 오래된 미커밋 본문 + 10분)`으로 PUT·커밋·alarm 처리 후 재설정한다.
- **고아 정리**: 커밋되지 않은 채 10분 넘은 `bodies` 행(및 R2 객체)을 alarm에서 삭제. R2 lifecycle rule(1일 후 만료)을 최후 안전망으로 설정한다 (버킷 설정이므로 `wrangler.toml`이 아니라 `wrangler r2 bucket lifecycle`로 등록).
- 최악 보관량: 20개 × 20 MiB(기본) ≈ 400 MiB, 사용자가 50 MiB로 올려도 ≈ 1 GiB (R2 무료 10GB 이내).

## 6. 클라이언트 공통 동작

### 6.1 상태와 설정
- 로컬 저장: `last_seq`, `last_applied_seq`, 가장 높은 `config_version`과 캐시된 config, 기기별 설정(전체 on/off, send/receive, 일시정지).
- 설정 의미
  | 설정 | 범위 | 동작 |
  |---|---|---|
  | 전체 off | 기기 | WebSocket 종료, 감시 중지 |
  | 일시정지 N분 | 기기 | 연결·목록 갱신 유지, 업로드·적용 중지. 만료 시 자동 재개 |
  | send off | 기기 | 로컬 복사를 업로드하지 않음 |
  | receive off | 기기 | 원격 항목 적용 안 함 (목록에는 표시) |
  | 이미지/파일 on/off, 최대 크기 | **vault 전체** | Mac에서만 편집 → 4.4의 config로 서버에 암호화 저장, 모든 기기가 따름 |
- 시작 시 `GET /v1/config` (또는 `hello`)로 설정을 받고, 실패하면 캐시 → 없으면 텍스트만.
- 설정 변경(Mac): 새 config 암호화 → `PUT /v1/config` (If-Match) → 충돌(412) 시 최신을 다시 받아 병합. 다른 기기는 `config` 이벤트로 반영.

### 6.2 송신 흐름
1. 클립보드 변경 감지 → 에코 마커 확인(6.4) → 표현 수집 → **4.5 판별/크기 규칙 적용**
2. 번들 인코딩 → header/body 암호화 → `PUT /v1/items/{id}/body` → `POST /v1/items`
3. 실패 시 지수 백오프 재시도(최대 3회, PUT/POST 모두 idempotent). 네트워크 오류로 끝내 실패하면 **가장 최근 항목 1개만 `pending`으로 보관**(더 새로운 복사가 이전 pending을 대체)하고 재연결 시 재전송한다. 알림은 재시도가 모두 실패한 시점에 한 번만 띄운다. 일시정지·send off·타입 off 때문에 보내지 않은 복사는 pending으로 만들지 않는다. 대용량은 진행률 표시.

### 6.3 수신 흐름
1. `item` 이벤트(또는 재접속 후 `GET /v1/items?since=last_seq`) 수신 → header 복호화 → 목록 갱신
2. 자기 기기 항목이 아니고, receive가 켜져 있고, 일시정지가 아니고, **항목의 종류가 현재 config에서 허용**되고, `purged`가 아니면 → 대상 후보. 후보 중 **가장 높은 seq 하나만** body를 받아 복호화 → 클립보드 적용 (FR-6). 더 새 항목이 도착하면 진행 중인 이전 다운로드는 취소한다.
   - **로컬 우선 규칙 (FR-6)**: 재접속·앱 재시작·일시정지/전체 off 해제 직후의 catch-up에서, 로컬 클립보드가 비어 있지 않고 그 정규화 해시가 **우리가 마지막으로 쓰거나 보낸 항목의 해시와 다르면** 사용자가 새로 복사한 것으로 보고 **원격 항목을 적용하지 않고 목록에만 반영**한다 (오프라인 중 복사한 더 새로운 내용을 덮어쓰지 않기 위함). 업로드 대기 중인 `pending` 항목(6.2-3)이 있으면 catch-up 적용 판단보다 먼저 업로드한다. 재부팅 직후처럼 클립보드가 비어 있으면 일반 규칙대로 최신 항목을 적용한다. 연결 중 도착하는 라이브 이벤트는 서버 seq 순서대로 항상 적용한다.
3. 적용 성공 후, **항목의 kinds에 image 또는 files가 있으면** `DELETE /v1/items/{id}/body`를 호출한다 (수신 후 삭제, FR-5). 복호화한 내용은 로컬 캐시 `incoming/<item_id>/`에 24시간(최대 200 MiB, LRU) 보관한다. 텍스트 항목은 삭제하지 않는다.
4. 첫 실행/페어링 직후에는 `last_seq`를 현재 최신으로 초기화해 **과거 항목을 자동 적용하지 않는다**.
5. 복호화 실패(AAD 불일치 등)는 항목을 폐기하고 오류 로그.
6. 사용자가 목록에서 항목을 선택하면: 로컬 캐시가 있으면 캐시에서, 없고 서버 본문이 남아 있으면 다운로드해서, 둘 다 없으면 "서버에서 삭제됨"으로 비활성 표시. 이 경우에도 성공 시 6.3-3의 삭제 규칙을 따른다.
- **송신 기기 자신**의 삭제된 항목: 로컬에 캐시(원본 파일 경로 또는 보관본)가 남아 있으면 복원 가능, 아니면 비활성.
- 알려진 한계: 3대 이상 기기에서는 처음 적용한 기기의 DELETE로 나머지 기기가 받을 수 없게 된다 (v1 검증 대상은 2대).

### 6.4 에코 방지 (FR-1)
- **1차: 비공개 마커.** 원격 항목을 클립보드에 쓸 때 `item_id`를 담은 private 타입을 함께 기록하고, 감시자는 이 타입이 있는 변경을 무시한다.
  - macOS: 커스텀 UTI `com.tobylee.clipsync.item`
  - Windows: `RegisterClipboardFormat("ClipSyncItemId")`
- **2차: 콘텐츠 해시 dedupe (필수 방어)**. 마지막 송·수신 항목 5개의 해시를 60초간 보관하고, 같은 해시의 변경은 업로드하지 않는다. OS가 표현을 재합성하거나 줄바꿈을 바꿀 수 있으므로 해시는 **정규화 후** 계산한다: 텍스트/HTML은 CRLF→LF, 끝의 NUL 제거, NFC; 이미지는 디코드한 픽셀 데이터; 파일은 (정규화된 이름, 크기, 내용) 목록.
- **해시 입력 (D-41)**: 정규화한 plain text가 있으면 그것을, 없으면 정규화한 HTML을 해시한다 (RDP를 통과하는 것은 plain text이므로).
- **RDP 클립보드 리디렉션 주의 (클라우드 Windows 환경)**: RDP는 Mac과 원격 Windows의 클립보드를 자체적으로 동기화한다. 이때 (a) 우리 앱과 무관하게 붙여넣기가 성공해 테스트가 오통과할 수 있고, (b) private 마커(커스텀 UTI/등록 포맷)는 RDP를 통과하지 못해 Windows가 방금 적용한 항목이 Mac 클립보드로 되돌아가 Mac 앱이 다시 업로드하는 **에코 루프**가 생길 수 있다. 따라서 해시 dedupe는 보조가 아니라 필수 방어이며, 개발·테스트는 RDP 클라이언트의 클립보드 리디렉션을 **끈 상태**를 기본으로 하고, 켠 상태의 에코 루프 테스트를 별도로 수행한다 (plan M4/M7).

### 6.5 재연결 (NFR-3)
- **catch-up 순서 (D-42)**: 연결 후 WS 이벤트는 `GET /v1/items?since=last_seq`가 끝날 때까지 버퍼링했다가 seq 순으로 처리한다. `seq <= last_seq`이거나 `device_id`가 자기 자신이면 건너뛴다. 첫 실행/페어링 직후 `last_seq`는 `hello.seq`로 초기화한다 (6.3-4).
- **하트비트**: 텍스트 메시지 `"ping"`을 보낸다 (Swift `sendPing()` 같은 제어 프레임은 DO auto-response와 매칭되지 않는다).
- 지수 백오프 + jitter (1s → 최대 60s). 성공 시 `last_seq` 기준 catch-up 후 6.3 적용.
- 슬립/웨이크 이벤트 시 즉시 재연결 (macOS `NSWorkspace.didWakeNotification`, Windows `SystemEvents.PowerModeChanged`).

### 6.6 알림
- 크기 초과, 연결 오류, 권한 필요 등 사용자 조치가 필요한 이벤트만 알림 (macOS `UNUserNotificationCenter`, Windows toast). 타입 off로 무시된 항목은 알림 없이 조용히 무시한다.

## 7. macOS 앱

- **식별자/빌드 (D-38)**: 앱 이름 `ClipSync`, 번들 ID `com.tobylee.clipsync`(영구), 서명 ID `Apple Development: tobyilee@gmail.com (P7H3D7D535)`(SHA-1 `6ED556371620F93225B112869C65FCBBF3CDFD6F`), `codesign --identifier com.tobylee.clipsync`. Xcode 프로젝트 대신 SwiftPM 실행 타깃 + 번들 조립·서명 스크립트로 빌드해 고정 경로(`~/Applications/ClipSync.app`)에 설치한다. provisioning profile이 필요한 entitlement(keychain-access-groups 등)는 쓰지 않는다. SwiftPM 리소스(`Bundle.module`)는 쓰지 않고 데이터는 Swift 소스로 내장한다.
- 형태: `LSUIElement` 메뉴바 앱 (`NSStatusItem`), SwiftUI 메뉴 + AppKit. **App Sandbox 비활성** (개인 설치용). 로그인 시 자동 시작은 `SMAppService.mainApp`.
- **서명 (권한 유지에 필수)**: 개인 설치용이라 공증·배포는 하지 않지만 **안정적인 서명 ID로 서명한다** — 무료 Apple Development 인증서(개인 팀) 또는 자체 서명 코드 서명 인증서. ad-hoc/무서명 빌드는 재빌드할 때마다 코드 정체성(designated requirement)이 달라져 pasteboard 허용, 파일 접근(TCC), Keychain 항목 접근이 초기화되고 프롬프트가 반복될 수 있다. 어느 ID를 쓸지는 M0에서 정하고, S-2에서 재빌드 후 권한 유지를 검증한다 (D-22).
- **배포 타겟**: 최소 macOS 14 (`SMAppService` 등 사용 API 기준; S-2에서 확정). pasteboard 프라이버시 신규 API(`accessBehavior`, `detect*`)는 SDK 헤더 기준 **macOS 15.4+** 이므로 **`#available(macOS 15.4, *)` 게이트**로 감싸고, 미지원 OS에서는 기존 방식으로 동작한다 (S-2, D-29). 개발 기기는 macOS 26.6.2, Xcode 27.0.
- **메뉴 구성 (FR-4)**
  ```
  ● 연결됨 · 동기화 켜짐 ✓
  일시정지 ▸ (15분 / 1시간 / 해제)
  보내기 ✓   받기 ✓
  ───────────────
  동기화 대상 ▸   ✓ 텍스트 (고정)   ☐ 이미지·스크린샷   ☐ 파일        ← vault 설정 (Mac 전용)
  미디어 최대 크기 ▸   5 MB / 10 MB / ✓ 20 MB / 50 MB               ← vault 설정 (Mac 전용)
  ───────────────
  최근 항목 ▸ (20개; 종류·미리보기·출처·시각; 삭제된 대용량 항목은 비활성 표시)
  ───────────────
  로그인 시 자동 시작 ✓ · 종료
  ```
- 감시: `NSPasteboard.general.changeCount`를 **약 250ms 타이머(tolerance 포함)** 로 폴링. 변경 시 6.2 실행.
- 소스 포맷 수집: HTML → (없으면) RTF를 `NSAttributedString`으로 HTML 변환 → plain text 순. 이미지는 PNG → (없으면) TIFF를 PNG로 변환. 규칙은 4.3.
- **pasteboard 개인정보 보호 (macOS 15.4+, S-2 실측 반영)**
  - SDK 헤더(`NSPasteboard.h`) 기준: `NSPasteboard.accessBehavior`(`.default`/`.ask`/`.alwaysAllow`/`.alwaysDeny`)와 `detectPatterns`/`detectValues`/`detectMetadata`는 **macOS 15.4+**. 프로그램적 내용 읽기는 `.ask`일 때 사용자 경고 대상이며, 사용자 조작에서 비롯된 붙여넣기 관련 접근은 항상 허용된다. 앱은 첫 경고가 뜬 뒤에야 시스템 설정 목록에 나타나고 그때 상태가 `.default`→`.ask`가 된다. `changeCount` 폴링은 경고 대상이 아니다.
  - `detect*`는 **패턴/메타데이터 감지용**(URL·이메일 등, `detectMetadata`는 첫 항목의 파일 content type 한정)이며 "무슨 종류인지" 사전 판별용이 아니다. **종류 판별은 `pasteboard.types`/`pasteboardItems[].types`로 한다**: 텍스트 `public.utf8-plain-text`, 파일 `public.file-url`(항목마다 1개), 이미지 `public.png`(+`public.tiff`). S-2에서 이 API들은 내용 읽기 없이 동작했다.
  - **실측(macOS 26.6.2, MDM 없음)**: 새 번들 ID의 앱(서명/ad-hoc 모두, Finder 직접 실행 포함)이 첫 실행부터 `accessBehavior == .alwaysAllow`였고 내용 읽기에서 경고가 뜨지 않았으며 "다른 앱에서 붙여넣기" 설정 화면도 보이지 않았다. 따라서 **온보딩은 상태 기반**으로 설계한다: `accessBehavior`를 런타임에 읽어(`#available(macOS 15.4, *)`) `.ask`/`.alwaysDeny`일 때만 메뉴에 **"권한 필요" 상태**와 시스템 설정 열기 버튼을 보이고, 그 외에는 안내 없이 동작한다. 경고 문구·설정 경로는 이 환경에서 재현되지 않아 확정하지 못했다 (다른 OS 버전/기본값에서 `.ask`를 만나면 그때 확인).
  - **HTML/RTF 처리 (D-39, D-40)**: RTF만 있는 소스는 `NSAttributedString`의 HTML 내보내기 결과에서 `<head>`의 `<style>` 블록을 `<body>` 내부 앞에 붙인 fragment로 전송한다(body만 취하면 서식이 사라진다). 원격 HTML을 쓸 때는 UTF-8 바이트로 기록하고 `<meta charset='utf-8'>`가 없으면 앞에 붙인다(한글·이모지 깨짐 방지).
  - 사전 판별: 읽기 전에 `types`로 종류를 보고, 꺼진 타입이면 내용을 읽지 않고 무시한다 (spec 4.5).
- 원격 항목 적용: 텍스트/HTML/PNG는 `NSPasteboardItem`으로 다중 표현을 한 번에 기록. 파일은 `~/Library/Caches/<bundle>/incoming/<item_id>/<sanitized name>`에 저장 후 file URL로 기록 (이 폴더가 6.3의 로컬 캐시). 24시간/200 MiB 기준으로 앱 시작 시와 매시간 정리.
- 대용량 업로드는 `URLSession` upload task(파일에서 스트리밍), 진행률은 메뉴 상태에 표시.
- 자격 증명 저장: Keychain. 로컬 파일 읽기 시 Desktop/Documents 등에서 TCC 프롬프트가 뜰 수 있음 → 온보딩 안내.

## 8. Windows 앱

- .NET 10 (LTS), **WinForms `NotifyIcon`** 트레이 앱 (WinUI 3는 트레이 미지원). self-contained 단일 파일 publish. 로그인 시 자동 시작은 `HKCU\...\Run`.
- 트레이 메뉴: Mac과 동일한 상태/일시정지/보내기·받기/최근 항목 + **"동기화 대상 / 최대 크기"는 읽기 전용 표시** ("Mac에서 변경").
- 감시: 메시지 전용 윈도우 + `AddClipboardFormatListener` → `WM_CLIPBOARDUPDATE`. 클립보드 접근은 **STA 스레드**, `OpenClipboard` 실패 시 재시도+백오프(5회, 20ms→320ms), ~100ms debounce.
- 표현 매핑
  | 종류 | 읽기/쓰기 포맷 |
  |---|---|
  | text | `CF_UNICODETEXT` |
  | html | `HTML Format` (`CF_HTML`; `StartHTML/EndHTML/StartFragment/EndFragment` 바이트 오프셋 헤더를 생성) |
  | image | `PNG` 등록 포맷 + `CF_DIBV5` **둘 다** 기록, 읽을 때는 PNG 우선 |
  | files | `CF_HDROP` (`%LOCALAPPDATA%\ClipboardSync\incoming\<item_id>\` = 로컬 캐시, Mac과 동일 정리 정책) |
- 읽을 때 `PNG`가 없고 `CF_DIBV5`/`CF_DIB`만 있으면 PNG로 변환해 전송한다 (알파 보존, 4.3).
- 우리가 쓰는 클립보드 항목에는 `CanUploadToCloudClipboard = 0`을 지정한다.
- 대용량 업로드/다운로드는 `HttpClient` 스트리밍(진행률 표시). 자격 증명: Credential Manager (DPAPI). 토스트 알림은 비패키지 앱이므로 AUMID 등록 필요 — 구현 시 확인.

## 9. 보안 정리 (NFR-1 검증 관점)
| 위협 | 대응 |
|---|---|
| 서버/네트워크 관찰자가 내용을 봄 | E2EE(AES-256-GCM), TLS. 서버는 타입도 알지 못함 (삭제도 수신 기기가 요청) |
| 서버가 메타데이터/항목/설정을 조작 | 항목·설정 모두 AAD 결속. 설정은 최고 version만 수용 |
| 서버가 설정을 숨기거나 되돌림 | 설정 없음/미수신은 텍스트만(fail-closed). 오래된 설정 재전송은 완전 방지 불가(수용) |
| 제3자가 서버 API/R2를 사용 | `VAULT_ID` allowlist, DO/R2 접근 전 Worker에서 차단, 401 rate limit, 51 MiB 하드 캡 |
| 저장 용량 남용/누수 | 20개 cap, 24h 만료, 수신 후 삭제, 고아 정리(alarm) + R2 lifecycle 안전망 |
| passphrase 오프라인 추측 | 생성형 고엔트로피 passphrase(~90bit) + PBKDF2 600k, 사용자 지정은 24자+ |
| 기기 분실 | Keychain/Credential Manager 보호. 키 교체: passphrase 변경 → 새 `VAULT_ID` 등록 → 서버 데이터 폐기 |
| Windows 클라우드/히스토리 유출 | `CanUploadToCloudClipboard=0` |
| RDP 클립보드 리디렉션(클라우드 Windows)으로 인한 에코 루프·테스트 오통과 | 정규화 해시 dedupe 필수, 테스트는 리디렉션 off 기본 + 별도 에코 루프 테스트 |
| 비밀번호 관리자 항목 | v1 제외. 일시정지로 수동 대응 |

## 10. 테스트 전략
- **공용 테스트 벡터** (`protocol/test-vectors.json`): passphrase → 키들 → 고정 nonce 암호문, 번들 인코딩 바이트, config 암호문(AAD 포함). Swift와 C# 테스트가 같은 파일을 읽는다. TypeScript 참조 구현(`protocol/ref`)은 벡터 생성기이자 **CLI 테스트 피어**(서버 WebSocket 테스트 클라이언트, 앱과의 상호 검증 상대)를 겸한다.
- 서버 (`vitest` + `@cloudflare/vitest-pool-workers`): 인증/allowlist, seq 단조성, 20개 cap(R2 객체 삭제 포함), 24h 만료(alarm), idempotent PUT/POST, **커밋·purged된 id 재PUT은 409**, 51 MiB 초과 413, 커밋 없는 PUT 고아 정리, **DELETE body 후 GET=410 및 `body_purged` 이벤트**, config `If-Match` 충돌, 큰 본문의 R2 스트리밍 경로.
- 클라이언트 단위: 번들 인코드/디코드, 4.5 타입 판별 (파일 off 시 파일명 폴백 없음, 이미지 off 시 텍스트만 남김), sanitize, CF_HTML 오프셋, 에코 방지(해시 정규화: CRLF/LF, 끝의 NUL, NFC), config fail-closed, NFC 정규화, RTF→HTML·TIFF→PNG·DIB→PNG 변환, 로컬 우선 규칙(pending 업로드 → catch-up 적용 생략).
- 수동 E2E 매트릭스: {텍스트, 리치 텍스트(HTML, 그리고 **RTF만 제공하는 Mac 앱: TextEdit/Notes/Pages/Word**), PNG(소형/대형), **TIFF/DIB만 제공하는 이미지**, 파일 1개/여러 개, **한글 파일명(Finder NFD 원본)**, 20MB 초과} × {Mac→Win, Win→Mac} × {타입 on/off, 온라인, 오프라인 후 재접속, 슬립/웨이크, 일시정지, 방향 토글} + 적용 후 서버 본문 삭제 확인.
- 추가 시나리오: (a) RDP 클립보드 리디렉션 **off**로 전체 매트릭스, (b) 리디렉션 **on**에서 에코 루프(무한 재업로드) 없음, (c) 오프라인 중 로컬에서 새로 복사 후 재접속 시 덮어쓰지 않고 pending이 먼저 업로드됨, (d) Mac 앱 재빌드 후 권한 프롬프트 재발 없음.

## 11. 구현 단계 제안
1. `protocol/` 확정 + 테스트 벡터 + TypeScript 참조 구현/CLI 테스트 피어 (Swift/C# 양쪽 통과)
2. 서버 (Worker + DO + R2) + 단위 테스트
3. macOS 앱 최소 기능 (텍스트 송수신) → 설정 메뉴(config) → 이미지/파일 → 수신 후 삭제
4. Windows 앱 동일 순서 (설정은 읽기 전용)
5. 오프라인/재연결/권한 온보딩/알림 마감, 수동 E2E 매트릭스

## 12. 알려진 한계 / 추후 과제
- 3대 이상 기기: 먼저 적용한 기기가 본문을 삭제하므로 나머지는 받지 못함.
- 삭제된 대용량 항목의 재복원은 로컬 캐시가 남아 있는 기기에서만 가능 (24시간/200 MiB LRU).
- 서버 롤백(오래된 config 재전송)은 완전히 방지하지 못함.
- 비밀번호 관리자 항목 자동 제외 미지원.
- 압축 미사용. 도입 시 raw deflate로 통일 (Apple `COMPRESSION_ZLIB`은 raw deflate).
- 서버 URL은 사용자가 배포한 `*.workers.dev`(또는 커스텀 도메인)를 앱 설정에 입력.
- **Cloudflare 요금제 (M0에서 확인)**: 웹 검색 요약 기준으로 SQLite 기반 Durable Object와 Rate Limiting binding은 Workers Free에서도 사용 가능하다 (Free는 DO 스토리지 객체당 1GB / 계정 5GB이나 대용량 본문은 R2에 있어 영향이 작음; [Durable Objects limits](https://developers.cloudflare.com/durable-objects/platform/limits), [Workers pricing](https://developers.cloudflare.com/workers/platform/pricing/), [Rate Limiting binding](https://developers.cloudflare.com/workers/runtime-apis/bindings/rate-limit/)). **R2 binding이 Free에서 가능한지는 자료가 엇갈려 미확인**이다. 요금제는 **Workers Free로 확정**(사용자 결정)이며, S-1을 Free에서 수행해 확정한다. Free의 일일 한도(요청 수, DO 실행 시간 등)는 M2에서 실측한다 (WebSocket hibernation과 heartbeat auto-response는 이 한도를 아끼기 위한 설계). R2 활성화에는 결제 수단이 필요할 수 있다.

## 13. 결정 로그 (Decision Log)
| ID | 결정 | 대안 | 근거 |
|---|---|---|---|
| D-1 | vault당 Durable Object 1개 + WebSocket Hibernation | KV/D1 + polling | 실시간 push와 순서 보장(seq), hibernation으로 유휴 비용 최소 |
| D-2 | PBKDF2-SHA256 600k + AES-256-GCM | Argon2id + ChaCha20-Poly1305 (libsodium) | 양 플랫폼 내장, 의존성 0. 약한 KDF는 생성형 고엔트로피 passphrase로 보완 |
| D-3 | 고정 versioned salt | 서버 저장 salt | 서버 왕복 없이 파생, 계정 없음. 고엔트로피 passphrase 강제가 전제 |
| D-4 | 첫 기기가 passphrase 생성(diceware 7단어) | 사용자 자유 입력 | 고정 salt의 안전성 확보. "동일 passphrase 입력" 방식은 유지 |
| D-5 | `VAULT_ID` secret allowlist + 401 rate limit | 공개 등록 + rate limit | 개인용이라 등록 절차 불필요, 타인의 DO/R2 사용 원천 차단 |
| D-6 | **(개정)** 본문 ≤1.25 MiB는 DO SQLite BLOB, 초과는 R2 | 전부 DO / 전부 R2 | 20MB 본문은 DO 행 한도(2MB)·메모리에 안 맞음. 소형 본문은 DO에 두어 텍스트 지연 최소화 |
| D-7 | header/body 분리 암호화 | 단일 blob | 목록 표시에 대용량 다운로드를 피함. 타입/파일명은 서버에 비노출 |
| D-8 | 순서는 서버 seq | 클라이언트 timestamp | 시계 불일치 배제, catch-up이 `since=seq`로 단순화 |
| D-9 | v1 압축 없음 | deflate | 이득 작고 상호운용 리스크만 증가 |
| D-10 | 본문은 HTTP, WS는 이벤트 통지 (≤32KB는 inline) | WS로 본문 전송 | 한도/과금 변화에 독립적, 재시도·idempotency 단순, 대용량 스트리밍 가능 |
| D-11 | 에코 방지 1차는 private 마커 타입 | 콘텐츠 해시만 | OS가 표현을 재합성해 해시가 흔들림. 해시는 보조 |
| D-12 | macOS 비샌드박스, changeCount 폴링 + 권한 온보딩 | 샌드박스 앱 | 임의 경로 파일 읽기 필요. macOS 26 paste 권한 대응 포함 |
| D-13 | Windows는 WinForms NotifyIcon | WPF+트레이 라이브러리, WinUI 3 | 트레이 지원이 가장 단순 |
| D-14 | ~~대형 스크린샷 skip~~ **폐기** → 미디어 기본 20MB(5/10/20/50 선택), 이미지·파일은 기본 OFF | 다운스케일/재인코딩 | 사용자 결정. 화질 손실 없이 한도를 상향 |
| D-15 | 20MB 본문은 Worker가 R2로 스트리밍, DO에는 메타만 | DO SQLite에 청크 분할 저장 | DO 메모리·복잡도 회피, R2 삭제/lifecycle 활용 |
| D-16 | "paste 완료" = **수신 기기의 클립보드 적용 완료**. 수신 기기가 `DELETE /v1/items/{id}/body` 호출 (header는 유지) | (a) 서버가 파일/이미지를 식별해 ack 시 삭제 (`ephemeral` 플래그), (b) 실제 붙여넣기 감지(delayed rendering/promised data) | (a) 서버가 타입을 알게 되어 NFR-1 위반 → 수신 측이 header로 종류를 알아 삭제. (b) 콜백이 OS 동기 호출+타임아웃이라 내부 네트워크 I/O가 위험. 실제 Cmd+V는 감지 불가 |
| D-17 | 이미지/파일 on/off·최대 크기는 **vault 전체 설정**을 E2EE config로 서버 저장, Mac에서만 편집, 없으면 텍스트만(fail-closed) | Mac 로컬 설정 | Mac 로컬 설정이면 Windows가 Mac이 끈 20MB 파일을 계속 업로드함. 서버는 값을 읽지 못하므로 강제는 송신 클라이언트가 수행 |
| D-18 | 파일이 켜져 있지 않으면 파일 항목 전체 무시(파일명 폴백 없음), 이미지 off면 이미지 표현만 제거 | 텍스트로 폴백 | Finder/Explorer가 파일명 문자열을 함께 넣어 의도치 않은 텍스트 전송이 생김 |
| D-19 | 서버 하드 캡 51 MiB는 상수, 사용자 한도(5–50 MiB)는 송신 클라이언트가 강제 | 서버가 사용자 한도 강제 | 서버가 config를 복호화할 수 없음 |
| D-20 | 업로드는 2단계: `PUT body`(스트림) → `POST` 커밋(header) | 길이 접두 단일 요청 | 스트림 분할 파싱이 복잡. 커밋된 항목만 이벤트로 노출, 고아는 alarm/lifecycle로 정리 |
| D-21 | Windows는 **.NET 10 (LTS)** | .NET 8 | .NET 8/9는 2026-11-10 지원 종료 (2026-09 기준 약 6주 남음, [.NET Blog](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)). .NET 10은 2028-11까지 지원 |
| D-22 | Mac 앱은 **안정적인 서명 ID**(무료 Apple Development 또는 자체 서명 인증서)로 서명, 공증은 안 함. 배포 타겟 명시 + 신규 API `#available` 게이트 | ad-hoc/무서명 | 재빌드마다 코드 정체성이 바뀌어 pasteboard·TCC·Keychain 권한이 초기화됨. D-12 보완 |
| D-23 | catch-up 시 **로컬 우선**: 사용자가 새로 복사한 로컬 내용이 있으면 원격 항목으로 덮어쓰지 않음. 네트워크 실패한 최신 로컬 항목 1개는 pending으로 보관·재전송 | 항상 최신 seq를 적용 | seq는 커밋된 항목에만 존재해, 오프라인 중 복사한 Y가 재접속 시 더 오래된 X에 덮어써짐. 사용자 승인 완료 (00 FR-6) |
| D-24 | 정규화 해시 dedupe를 필수 방어로 격상 (CRLF/LF, NUL, NFC, 이미지는 픽셀). RDP 리디렉션 off를 테스트 기본으로 | 마커만 신뢰 | RDP는 클립보드를 자체 동기화하고 private 마커를 전달하지 못해 에코 루프·오통과 가능 |
| D-25 | 파일명·preview는 송신 시 NFC 정규화 (본문 텍스트는 원문 유지) | 무처리 | Finder의 NFD 한글 파일명이 Windows에서 자모 분리로 보임 |
| D-26 | 소스 포맷 변환: RTF→HTML(Mac), TIFF→PNG(Mac), DIB/DIBV5→PNG(Windows) | HTML/PNG만 지원 | TextEdit/Notes/Pages/Word는 RTF만, 일부 앱은 TIFF/DIB만 제공해 그대로면 "리치 텍스트/이미지 지원"이 실패 |
| D-27 | 이미 커밋/purged된 id의 `PUT /body`는 409 | 덮어쓰기 허용 | DELETE 후 재시도된 PUT이 삭제된 본문을 되살리는 것을 방지 |
| D-28 | WebSocket 하트비트: 클라이언트 30초 ping, 90초 pong 없으면 재연결 | 미정 | 유휴 연결 끊김의 조기 감지 |
| D-29 | pasteboard 신규 API 게이트는 `#available(macOS 15.4, *)`. 종류 판별은 `pasteboard.types`로, 권한 온보딩은 `accessBehavior` 런타임 상태 기반(`.ask`/`.alwaysDeny`일 때만 "권한 필요") | `detect*`로 사전 판별, 항상 권한 안내 온보딩 | S-2: `detect*`는 종류 판별 API가 아님(파일 content type 한정), 이 환경에서 `.alwaysAllow` 기본값이라 경고가 재현되지 않음 |
| D-30 | HKDF는 빈 salt, `info`는 라벨 UTF-8 원문(NUL 없음), passphrase는 NFKD→UTF-8 바이트로 PBKDF2, `vault_id`는 소문자 hex | HKDF에 고정 salt 추가 | 두 플랫폼 기본 동작이 동일해 상호운용 위험이 가장 낮음. PBKDF2가 이미 salt 적용. S-4 착수 전 모호성 제거 |
| D-31 | 복사에 폴더가 하나라도 포함되면 항목 전체 무시 + 알림 | 폴더만 건너뛰고 나머지 전송 / v1에서 디렉터리 엔트리 지원 | v1 번들에 디렉터리 타입이 없고 수신 측이 경로 구분자를 제거함. S-3에서 Explorer HDROP이 폴더를 포함함을 확인. 일부만 전달되어 조용히 누락되는 것을 피함(D-18과 같은 방향) |
| D-32 | passphrase는 NFKD 뒤 공백 집합의 연속을 U+0020 하나로, 양끝 제거(대소문자 유지) | NFKD만 | 두 번째 기기의 공백 오타가 다른 vault_id → 원인 모를 401이 되는 것을 방지. 서버 데이터가 생기기 전(M1)에 확정 |
| D-33 | UUID 와이어/AAD 표현은 RFC 4122 순서 16B, 문자열/키는 소문자 hex. 번들은 정렬·엄격 규칙(PROTOCOL.md), JSON(header/config)은 비정규 → 벡터는 고정 평문 바이트와 파싱 필드로 비교 | 언어 기본 표현/인코더 출력 바이트 비교 | C# Guid 혼합 엔디언, JSON 이스케이프·키 순서가 언어마다 달라 상호 복호화 실패를 만들 수 있음 |
| D-34 | R2 본문 키는 업로드마다 고유(`<vault_id>/<hex(id)>/<nonce>`, `bodies.r2_key`). Worker는 스트리밍 전 DO `begin`, 후 `finish`(재검사·교체·이전 객체 삭제)를 호출 | 고정 키 `<vault_id>/<hex(id)>` | 고정 키에서는 DELETE/커밋 직전에 통과한 재시도 PUT이 객체를 되살리거나 덮어써 DO가 모르는 고아가 생김(D-27 위반). 고유 키면 finish에서 원자적으로 판정 |
| D-35 | id/device_id는 어디서나 소문자 hex, blob(header, inline_body, config)은 패딩 있는 표준 base64, Bearer만 패딩 없는 base64url(엄격, 32B) | 혼용 | Swift `Data(base64Encoded:)`가 URL-safe를 거부하는 등 상호운용 오류를 막음. `body_purged.id`의 `<b64>` 표기를 hex로 정정 |
| D-36 | `hello.seq`는 마지막으로 부여된 seq(`sqlite_sequence`), 만료·cap 후에도 줄지 않음. WS의 `last_seq` 파라미터는 폐지 | `MAX(seq)`, `last_seq` 사용 | 행이 지워지면 MAX가 0으로 떨어져 catch-up이 혼동됨. catch-up은 `GET /items?since=`로 충분 |
| D-37 | purged 항목도 20개 cap에 포함. 만료는 행+본문 삭제(GET 404). 상태 코드: 411 CL 없음, 412 If-Match 불일치, 404 미커밋 DELETE, 409 본문 없는 커밋, 413 header/config blob 64 KiB 초과 | 미정 | 구현이 암묵적으로 정하면 클라이언트와 어긋남 |
| D-38 | Mac 앱: 이름 ClipSync, 번들 ID `com.tobylee.clipsync`, Apple Development 서명(SHA-1 고정), SwiftPM 실행 타깃 + 번들 조립 스크립트, `~/Applications` 고정 설치, profile 필요한 entitlement 금지 | Xcode 프로젝트/xcodegen, 자체 서명 ID | xcodegen/tuist 미설치, 의존성 최소. 번들 ID는 Keychain·TCC가 묶이는 영구 값(사용자 확정) |
| D-39 | RTF→HTML은 내보낸 문서의 `<style>`을 body 내용 앞에 붙인 fragment | body만 취함 / 전체 문서 전송 | `NSAttributedString` 출력은 서식을 head의 클래스 스타일에 둠. body만 취하면 서식 손실, 전체 문서는 4.3의 "fragment만" 규칙과 충돌 |
| D-40 | 원격 HTML 쓰기는 UTF-8 + `<meta charset='utf-8'>` 보장 | 원문 그대로 | macOS 앱이 charset 없는 `public.html`을 다른 인코딩으로 읽어 한글·이모지가 깨질 수 있음 |
| D-41 | 해시 입력은 정규화 plain text 우선, 없으면 정규화 HTML | 항상 HTML | RDP 등이 재합성해도 plain text는 유지됨 |
| D-42 | catch-up 동안 WS 이벤트 버퍼링 후 seq 순 처리, `seq<=last_seq`·자기 기기 항목 건너뜀, 첫 실행은 `last_seq=hello.seq`, 하트비트는 텍스트 `ping` | 즉시 처리 | 조회와 이벤트가 겹쳐 순서·중복이 어긋나는 것을 방지, 과거 항목 자동 적용 방지 |
| D-43 | passphrase 생성은 EFF large wordlist(7776) 7단어를 `SecRandomCopyBytes` + 거절 샘플링으로 뽑고 ClipSyncCore에 둠 (단어 목록은 Swift 소스로 내장) | 나머지 연산(편향) | 7776은 2의 거듭제곱이 아니라 modulo는 편향됨 |
