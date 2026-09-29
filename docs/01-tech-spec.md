# Clipboard Sync — 기술 명세서 (Tech Spec)

> 상태: 설계 확정 (구현 미착수)
> 작성일: 2026-09-29 / 개정: 2026-09-29 (R2 대용량 본문, 수신 후 삭제, vault 설정)
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
│   └─ test-vectors.json  # passphrase→키→암호문, 번들/설정 인코딩 벡터 (Swift/C# 테스트 공용)
├─ server/                # Cloudflare Worker + DO (TypeScript, wrangler)
├─ mac/                   # Swift 앱 (Xcode project + Swift Package)
└─ windows/               # .NET 8 솔루션
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
enc_key     = HKDF(master, info="clipsync/v1/enc",  32B)
auth_token  = HKDF(master, info="clipsync/v1/auth", 32B)
vault_id    = hex(SHA-256(auth_token))
```
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
- `item_id`: 클라이언트가 만드는 UUID v4 (16B). 에코 방지 마커와 AAD에도 쓴다.
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
  purged      INTEGER NOT NULL DEFAULT 0  -- 1 = 본문 삭제됨 (수신 후 삭제 / 만료 정리)
);
CREATE TABLE bodies (
  id          BLOB PRIMARY KEY,         -- item_id (PUT 시점에 생성, 커밋 전에는 items 행 없음)
  storage     TEXT NOT NULL,            -- 'do' | 'r2'
  size        INTEGER NOT NULL,
  body        BLOB,                     -- storage='do'일 때만 (BLOB, base64 아님)
  created_at  INTEGER NOT NULL,
  committed   INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE config (
  id      INTEGER PRIMARY KEY CHECK (id = 1),
  version INTEGER NOT NULL,             -- PUT마다 +1
  blob    BLOB NOT NULL                 -- 암호화된 vault 설정
);
```
- R2 객체 키: `<vault_id>/<hex(item_id)>`. 삭제는 항상 DO가 R2 binding으로 수행한다 (수신 후 삭제, 20개 cap 초과분, 만료, 고아 정리).
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
  - 이미지의 정규 포맷은 **PNG**.
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
1. **파일 URL/HDROP이 있으면 파일 항목.** `files=false`이면 항목 전체 무시 (Finder/Explorer가 함께 넣는 파일명 문자열로 폴백하지 않는다). `files=true`이면 body에 파일만 담는다.
2. 그 외 이미지 표현이 있으면: `images=false`이면 이미지 표현을 버리고 text/html 표현이 남으면 그것만 전송, 남는 게 없으면 무시. `images=true`이면 이미지(+남은 텍스트 표현)를 전송.
3. 텍스트/HTML만 있으면 텍스트 항목.

크기: body에 image/file 엔트리가 있으면 `max_media_bytes` (평문 합계) 이하, 없으면 1 MiB 이하. 초과 시 업로드하지 않고 알림 (타입 off로 무시된 경우는 조용히 무시).

서버 하드 캡: body 암호문 **51 MiB** 초과 시 413 (상수; 사용자 설정값과 무관).

## 5. 서버 API (Cloudflare Worker)

### 5.1 인증 / 남용 방지 (D-5)
- 클라이언트는 모든 요청에 `Authorization: Bearer <base64url(auth_token)>` 를 보낸다 (WebSocket upgrade 포함).
- Worker는 `SHA-256(auth_token)` 이 wrangler secret **`VAULT_ID`** 와 (상수시간 비교로) 일치하는지 확인한다. **불일치 시 DO/R2를 건드리지 않고 401**. 401은 IP 기준 rate limit (Workers Rate Limiting binding).
- 본문 업로드는 `Content-Length` 필수, 51 MiB 초과는 인증 통과 후에도 즉시 413 (본문을 읽기 전에 거부).
- 최초 설정: 첫 기기가 passphrase를 생성하면 앱이 `vault_id`를 표시 → 사용자가 `wrangler secret put VAULT_ID` 로 등록. 이후 기기는 passphrase와 서버 URL만 입력.
- Workers 요청 본문 한도는 플랜에 따라 100MB 이상이라 51 MiB 업로드에 충분하다 (조사 확인).

### 5.2 엔드포인트
| Method / Path | 설명 |
|---|---|
| `GET /v1/ws?device_id=&last_seq=` | WebSocket upgrade. 연결 직후 서버가 `hello` 전송 |
| `PUT /v1/items/{id}/body` | **1단계: 본문 업로드.** 헤더: `X-Device-Id`, `Content-Length`. 본문(binary)은 body 암호문. ≤1.25 MiB는 DO(`bodies`)에, 초과는 Worker가 **R2로 스트리밍** (`FixedLengthStream`), DO에는 메타만 기록. 같은 id 재PUT은 덮어쓰기 (재시도 안전) |
| `POST /v1/items` | **2단계: 커밋.** 헤더: `X-Item-Id`, `X-Device-Id`, `X-Created-At`. 본문(binary)은 header 암호문. 해당 id의 본문이 있어야 하며, seq 부여 후 broadcast. 응답 `{seq}`. 같은 id 재커밋은 기존 seq 반환 (idempotent) |
| `GET /v1/items?since=<seq>` | seq 초과 항목 목록 (최대 20개, 만료 제외, purged 여부 포함). 원소: `seq, id, device_id, created_at, header(base64), body_size, purged` |
| `GET /v1/items/{id}/body` | body 암호문 (스트리밍). 404=없음/만료, **410=purged(수신 후 삭제됨)** |
| `DELETE /v1/items/{id}/body` | **수신 기기가 클립보드 적용 후 호출** (idempotent). 본문(DO 행 또는 R2 객체) 삭제, `purged=1`, `body_purged` 이벤트 broadcast. header는 유지 |
| `GET /v1/config` | `{version, blob(base64)}`; 설정이 없으면 404 (= 텍스트만) |
| `PUT /v1/config` | 헤더 `If-Match: <version>`(최초는 0). 낙관적 동시성; 성공 시 version+1, `config` 이벤트 broadcast. UI는 Mac만 노출하지만 API는 동일 토큰으로 동작 |

- 업로드 순서(PUT→POST)는 커밋된 항목만 이벤트로 보이게 하고, 대용량 스트림을 DO 메모리에 올리지 않기 위한 것이다 (D-15, D-20).
- 클라이언트는 대용량 업로드/다운로드 동안 진행률을 표시하고 긴 타임아웃(예: 5분)을 사용한다.

### 5.3 WebSocket 메시지 (JSON)
- 서버→클라이언트
  - `{"t":"hello","seq":<최신 seq>,"config":{"version":N,"blob":"<b64>"}|null}`
  - `{"t":"item","seq","id","device_id","created_at","header":"<b64>","body_size","inline_body":"<b64>|null"}` — body 암호문이 32KB 이하면 inline (2초 목표, NFR-2). 업로드한 기기의 소켓에는 보내지 않는다.
  - `{"t":"body_purged","id":"<b64>"}` — 다른 기기가 적용을 마쳐 본문이 삭제됨. 클라이언트는 해당 항목을 "서버에서 삭제됨"으로 표시 (로컬 캐시가 있으면 복원 가능).
  - `{"t":"config","version":N,"blob":"<b64>"}`
- 클라이언트→서버: 없음. Heartbeat는 DO **auto-response**(`"ping"`→`"pong"`)로 처리해 DO를 깨우지 않는다.

### 5.4 Durable Object 동작
- SQLite-backed DO + **WebSocket Hibernation API**. R2 binding 보유.
- 커밋 시 `seq` 부여 → 20개 초과분의 항목·본문(DO 행/R2 객체) 즉시 삭제 (FR-5) → 다른 소켓에 `item` 이벤트 broadcast.
- 만료(24h): DO **alarm**으로 만료 항목의 본문 삭제, 읽기 시에도 `expires_at > now` 필터.
- **고아 정리**: 커밋되지 않은 채 10분 넘은 `bodies` 행(및 R2 객체)을 alarm에서 삭제. R2 lifecycle rule(1일 후 만료)을 최후 안전망으로 설정.
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
3. 실패 시 지수 백오프 재시도(최대 3회, PUT/POST 모두 idempotent), 이후 알림. 대용량은 진행률 표시.

### 6.3 수신 흐름
1. `item` 이벤트(또는 재접속 후 `GET /v1/items?since=last_seq`) 수신 → header 복호화 → 목록 갱신
2. 자기 기기 항목이 아니고, receive가 켜져 있고, 일시정지가 아니고, **항목의 종류가 현재 config에서 허용**되고, `purged`가 아니면 → 대상 후보. 후보 중 **가장 높은 seq 하나만** body를 받아 복호화 → 클립보드 적용 (FR-6). 더 새 항목이 도착하면 진행 중인 이전 다운로드는 취소한다.
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
- **2차: 콘텐츠 해시 dedupe** (OS의 표현 재합성으로 해시가 흔들릴 수 있어 보조 수단).

### 6.5 재연결 (NFR-3)
- 지수 백오프 + jitter (1s → 최대 60s). 성공 시 `last_seq` 기준 catch-up 후 6.3 적용.
- 슬립/웨이크 이벤트 시 즉시 재연결 (macOS `NSWorkspace.didWakeNotification`, Windows `SystemEvents.PowerModeChanged`).

### 6.6 알림
- 크기 초과, 연결 오류, 권한 필요 등 사용자 조치가 필요한 이벤트만 알림 (macOS `UNUserNotificationCenter`, Windows toast). 타입 off로 무시된 항목은 알림 없이 조용히 무시한다.

## 7. macOS 앱

- 형태: `LSUIElement` 메뉴바 앱 (`NSStatusItem`), SwiftUI 메뉴 + AppKit. **App Sandbox 비활성** (개인 설치용). 로그인 시 자동 시작은 `SMAppService.mainApp`.
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
- **pasteboard 개인정보 보호 (macOS 26 대응)**
  - macOS 26부터 사용자 조작과 무관한 프로그램적 pasteboard **내용 읽기**는 시스템 경고/권한 프롬프트 대상이며, `NSPasteboard.accessBehavior`(허용/거부/질문), 데이터를 읽지 않고 종류만 확인하는 `detect*` API가 추가된다. `changeCount` 폴링 자체는 프롬프트 대상이 아니다. (출처: [Michael Tsai — Pasteboard Privacy Preview in macOS 15.4](https://mjtsai.com/blog/2025/05/12/pasteboard-privacy-preview-in-macos-15-4/), [9to5Mac](https://9to5mac.com/2025/05/12/macos-16-clipboard-privacy-protection/))
  - 따라서 (a) **온보딩**에서 첫 읽기를 유도하고 시스템 설정의 "다른 앱에서 붙여넣기"를 **허용**으로 바꾸도록 안내, (b) 읽기 전에 `detect*`/타입 목록으로 읽을 가치가 있는 타입인지 먼저 판별 (타입이 꺼져 있으면 내용을 읽지 않고 무시), (c) 메뉴에 **"권한 필요" 상태**와 시스템 설정 열기 버튼.
  - 구현 시 실제 API 시그니처와 설정 경로는 Xcode SDK 문서로 재확인한다 (웹 요약 기반 정보).
- 원격 항목 적용: 텍스트/HTML/PNG는 `NSPasteboardItem`으로 다중 표현을 한 번에 기록. 파일은 `~/Library/Caches/<bundle>/incoming/<item_id>/<sanitized name>`에 저장 후 file URL로 기록 (이 폴더가 6.3의 로컬 캐시). 24시간/200 MiB 기준으로 앱 시작 시와 매시간 정리.
- 대용량 업로드는 `URLSession` upload task(파일에서 스트리밍), 진행률은 메뉴 상태에 표시.
- 자격 증명 저장: Keychain. 로컬 파일 읽기 시 Desktop/Documents 등에서 TCC 프롬프트가 뜰 수 있음 → 온보딩 안내.

## 8. Windows 앱

- .NET 8, **WinForms `NotifyIcon`** 트레이 앱 (WinUI 3는 트레이 미지원). self-contained 단일 파일 publish. 로그인 시 자동 시작은 `HKCU\...\Run`.
- 트레이 메뉴: Mac과 동일한 상태/일시정지/보내기·받기/최근 항목 + **"동기화 대상 / 최대 크기"는 읽기 전용 표시** ("Mac에서 변경").
- 감시: 메시지 전용 윈도우 + `AddClipboardFormatListener` → `WM_CLIPBOARDUPDATE`. 클립보드 접근은 **STA 스레드**, `OpenClipboard` 실패 시 재시도+백오프(5회, 20ms→320ms), ~100ms debounce.
- 표현 매핑
  | 종류 | 읽기/쓰기 포맷 |
  |---|---|
  | text | `CF_UNICODETEXT` |
  | html | `HTML Format` (`CF_HTML`; `StartHTML/EndHTML/StartFragment/EndFragment` 바이트 오프셋 헤더를 생성) |
  | image | `PNG` 등록 포맷 + `CF_DIBV5` **둘 다** 기록, 읽을 때는 PNG 우선 |
  | files | `CF_HDROP` (`%LOCALAPPDATA%\ClipboardSync\incoming\<item_id>\` = 로컬 캐시, Mac과 동일 정리 정책) |
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
| 비밀번호 관리자 항목 | v1 제외. 일시정지로 수동 대응 |

## 10. 테스트 전략
- **공용 테스트 벡터** (`protocol/test-vectors.json`): passphrase → 키들 → 고정 nonce 암호문, 번들 인코딩 바이트, config 암호문(AAD 포함). Swift와 C# 테스트가 같은 파일을 읽는다.
- 서버 (`vitest` + `@cloudflare/vitest-pool-workers`): 인증/allowlist, seq 단조성, 20개 cap(R2 객체 삭제 포함), 24h 만료(alarm), idempotent PUT/POST, 51 MiB 초과 413, 커밋 없는 PUT 고아 정리, **DELETE body 후 GET=410 및 `body_purged` 이벤트**, config `If-Match` 충돌, 큰 본문의 R2 스트리밍 경로.
- 클라이언트 단위: 번들 인코드/디코드, 4.5 타입 판별 (파일 off 시 파일명 폴백 없음, 이미지 off 시 텍스트만 남김), sanitize, CF_HTML 오프셋, 에코 방지, config fail-closed.
- 수동 E2E 매트릭스: {텍스트, 리치 텍스트, PNG(소형/대형), 파일 1개/여러 개, 20MB 초과} × {Mac→Win, Win→Mac} × {타입 on/off, 온라인, 오프라인 후 재접속, 슬립/웨이크, 일시정지, 방향 토글} + 적용 후 서버 본문 삭제 확인.

## 11. 구현 단계 제안
1. `protocol/` 확정 + 테스트 벡터 (Swift/C# 양쪽 통과)
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
- **R2 활성화 필요**: 계정에서 R2를 켜야 하고 결제 수단 등록이 필요할 수 있음 (무료 한도 내 예상) — 배포 시 확인.

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
