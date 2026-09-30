# ClipSync

Mac과 Windows 사이에서 **복사한 내용을 자동으로 주고받는** 개인용 클립보드 동기화 도구입니다.
한쪽에서 복사하면 다른 쪽에서 바로 붙여넣을 수 있고, 모든 내용은 **종단간 암호화(E2EE)** 되어 서버는 무엇을 복사했는지 알 수 없습니다.

- **macOS** 메뉴바 앱 (Swift) · **Windows** 트레이 앱 (.NET 10) · **서버** Cloudflare Workers + Durable Object + R2
- 서버는 본인 Cloudflare 계정에 배포합니다 (Workers Free 플랜으로 동작).

> 상태: 개발 일시 중단 — M0~M5 완료, M6(수신 후 삭제·오프라인)은 구현·Mac 확인까지 완료, M7(마감) 미착수. 남은 일은 [남은 작업](docs/04-remaining.md)에 정리되어 있습니다.

## 주요 기능

| 기능 | 내용 |
|---|---|
| 양방향 자동 동기화 | 텍스트, 서식 있는 텍스트(HTML, Mac의 RTF 포함), 이미지(PNG), 파일 |
| 종단간 암호화 | passphrase에서 파생한 키로 AES-256-GCM. 서버는 내용·종류·파일명을 보지 못함 |
| 동기화 대상 설정 | 이미지·파일 on/off, 미디어 최대 크기(5/10/20/50 MB). Mac에서 바꾸면 모든 기기가 따름. **기본은 텍스트만** |
| 짧은 보관 | 서버에는 최근 20개, 최대 24시간. 이미지·파일은 받는 기기가 적용하면 서버에서 바로 삭제 |
| 최근 항목 | 메뉴에서 최근 20개를 보고 다시 클립보드에 넣기 (서버에서 지워진 항목은 로컬 캐시가 있으면 복원) |
| 오프라인 대응 | 끊긴 동안 보내지 못한 마지막 복사를 보관했다가 재연결 시 전송. 그사이 로컬에서 새로 복사했다면 원격 항목으로 덮어쓰지 않음 |
| 에코 방지 | 앱이 쓴 클립보드에는 전용 마커를 붙이고, 내용 해시로 한 번 더 걸러 무한 재업로드를 막음 |
| 제어 | 동기화 on/off, 일시정지(15분/1시간), 보내기·받기 방향별 on/off, 로그인 시 자동 시작 |

## 구성

```
 ┌──────────────┐   HTTPS (본문 업로드/다운로드)   ┌───────────────────────────────┐
 │  macOS 앱     │ ───────────────────────────────▶ │ Cloudflare Worker             │
 │  (메뉴바)     │ ◀─────── WebSocket (이벤트) ───── │  └ Durable Object (vault 1개)  │
 └──────────────┘                                   │     · 항목 메타·작은 본문(SQLite) │
 ┌──────────────┐                                   │  └ R2 (1.25 MiB 초과 본문)      │
 │  Windows 앱   │ ◀──────────────────────────────▶ └───────────────────────────────┘
 │  (트레이)     │        모든 본문·헤더·설정은 기기에서 암호화된 채로 오간다
 └──────────────┘
```

## 저장소 구조

| 경로 | 내용 |
|---|---|
| `server/` | Cloudflare Worker + Durable Object (TypeScript, `vitest` 테스트, 배포 스모크 스크립트) |
| `mac/ClipSyncCore/` | 공용 로직 Swift 패키지 (암호·번들·서버 클라이언트·규칙) |
| `mac/ClipSyncApp/` | macOS 메뉴바 앱 (SwiftPM 실행 타깃), `mac/scripts/build-app.sh`로 조립·서명·설치 |
| `windows/ClipSync.Core/` | 공용 로직 C# 라이브러리 (OS 무관, Mac에서도 테스트 가능) |
| `windows/ClipSync.App/` | Windows 트레이 앱 (WinForms, 실행 파일 `ClipSync.exe`) |
| `protocol/` | 바이트 포맷 명세, 공용 테스트 벡터, TypeScript 참조 구현과 CLI 테스트 피어 |
| `docs/` | 요구사항·기술 명세·구현 계획·핸드오프·스파이크 결과 |

## 설치

### 준비물

- **서버:** Cloudflare 계정(R2 활성화), Node.js 24, `wrangler`
- **Mac:** macOS 14 이상, Xcode(Swift 6), 코드 서명 인증서 1개(자체 서명 가능)
- **Windows:** .NET SDK 10.0.401 (`windows/global.json`)

### 1. 서버 배포

```sh
cd server
npm ci
npx wrangler login
npx wrangler r2 bucket create <버킷 이름>
npx wrangler r2 bucket lifecycle add <버킷 이름> expire-1d --expire-days 1   # 고아 객체 안전망
```

`server/wrangler.toml`의 기본값은 개발용(`clipsync-dev`)입니다. 필요하면 Worker 이름(`name`)과 버킷(`bucket_name`)을 바꾼 뒤 배포합니다.

```sh
npx wrangler deploy        # 출력된 https://<이름>.<서브도메인>.workers.dev 가 서버 URL
```

`VAULT_ID`는 다음 단계에서 Mac 앱이 보여 주는 값으로 등록합니다.

### 2. Mac 앱

```sh
cd mac
./scripts/build-app.sh     # 빌드 → ~/Applications/ClipSync.app 에 서명·설치
open ~/Applications/ClipSync.app
```

- 서명 ID는 `mac/scripts/build-app.sh`의 `SIGN_SHA1`에 고정되어 있습니다. 다른 Mac에서는 키체인 접근에서 코드 서명용 인증서를 만들고 그 SHA-1(`security find-identity -v -p codesigning`)으로 바꿉니다. ad-hoc 서명은 재빌드마다 권한이 초기화되므로 쓰지 않습니다.
- 처음 실행하면 설정 창이 뜹니다.
  1. 서버 URL을 입력하고 **새 passphrase 만들기**를 고릅니다 (EFF 단어 7개).
  2. 화면에 보이는 `vault_id`를 서버에 등록합니다: `cd server && npx wrangler secret put VAULT_ID`
  3. **연결 테스트 후 저장**을 누릅니다. passphrase는 Keychain에 저장됩니다.
- passphrase는 **다른 기기에 그대로 입력**해야 하고, 잃어버리면 복구할 수 없습니다 (새 vault를 만들어야 함).
- 메뉴의 **동기화 대상 / 미디어 최대 크기**에서 이미지·파일 동기화를 켭니다 (기본은 텍스트만).

### 3. Windows 앱

```bat
dotnet build windows\ClipSync.App -c Release
windows\ClipSync.App\bin\Release\net10.0-windows\ClipSync.exe
```

- 설정 창에 서버 URL과 **Mac에서 만든 passphrase**를 입력하고 저장합니다. passphrase는 Credential Manager에 저장됩니다.
- 개발 중에는 `windows\dev-run.cmd` 하나로 실행 중인 앱 종료 → 빌드 → 재실행을 합니다.
- `ClipSync.exe --selftest`로 클립보드·자격 증명·서버 연결을 한 번에 점검할 수 있습니다 (클립보드 내용을 덮어씀).
- 원격 Windows(RDP, Cloud PC)에서 쓸 때는 RDP 클라이언트의 **클립보드 리디렉션을 끄는 것**을 권장합니다. 켜 두면 RDP가 따로 클립보드를 동기화합니다.

## 사용

- 평소에는 그냥 복사하고 붙여넣으면 됩니다. 상태는 메뉴바/트레이 아이콘과 메뉴 첫 줄에 표시됩니다.
- **최근 항목**에서 항목을 고르면 다시 클립보드에 넣습니다. "캐시에서 복원"은 서버에서는 지워졌지만 이 기기에 캐시가 남은 항목입니다.
- 크기 초과, 폴더 복사, 전송 실패 같은 일은 알림(Mac 알림 센터 / Windows 풍선 알림)과 메뉴 메시지로 알려 줍니다.
- 폴더(와 Mac의 `.app` 같은 패키지)가 섞인 복사는 보내지 않습니다.

| 위치 | macOS | Windows |
|---|---|---|
| 로그 (내용은 기록하지 않음) | `~/Library/Logs/ClipSync/clipsync.log` | `%LOCALAPPDATA%\ClipboardSync\logs\` (메뉴 "로그 폴더 열기") |
| 받은 이미지·파일 캐시 (24시간·200 MiB) | `~/Library/Caches/com.tobylee.clipsync/incoming/` | `%LOCALAPPDATA%\ClipboardSync\incoming\` |
| 설정 | `defaults read com.tobylee.clipsync` | `%LOCALAPPDATA%\ClipboardSync\settings.json` |

## 보안

- passphrase → PBKDF2-SHA256(600,000회) → HKDF로 암호화 키와 인증 토큰을 나눕니다. 항목의 헤더·본문과 설정은 각각 AES-256-GCM으로 암호화되며, 항목 id·기기·시각을 AAD로 묶어 바꿔치기를 막습니다.
- 서버는 인증 토큰의 SHA-256이 `VAULT_ID` secret과 같을 때만 요청을 받습니다. 다른 요청은 DO/R2에 닿기 전에 401로 거절하고 IP별로 제한합니다.
- 서버가 볼 수 있는 것은 항목 id, 기기 id, 시각, 암호문 크기, 순번뿐입니다. 종류·파일명·미리보기·설정 값은 암호문 안에만 있습니다.
- 받은 파일 이름은 디스크에 쓰기 전에 정리합니다 (경로 구분자·예약 이름·길이 제한, [PROTOCOL.md](protocol/PROTOCOL.md)와 테스트 벡터로 두 플랫폼 동일).
- Windows에서 앱이 쓴 항목은 클라우드 클립보드 업로드에서 제외합니다 (`CanUploadToCloudClipboard=0`).

## 개발

```sh
# 서버 단위 테스트 (Durable Object·R2 포함, 로컬 에뮬레이션)
cd server && npm test

# 프로토콜 참조 구현과 공용 테스트 벡터 (npm run gen 으로 재생성, 직접 수정 금지)
cd protocol/ref && npm test

# Swift / C# 공용 로직 (같은 벡터를 읽는다)
cd mac/ClipSyncCore && swift test
dotnet test windows/ClipSync.sln
```

**서버 교차 테스트(선택):** 로컬 서버를 띄우고 환경 변수를 주면 TS·Swift·C#가 실제 서버를 거쳐 서로의 암호문을 여는지 확인합니다. 아래 passphrase는 테스트 벡터에 공개된 **테스트 전용** 값이며 로컬 서버의 `.dev.vars`에 맞춰져 있습니다.

```sh
cd server && npx wrangler dev --port 8787        # 다른 터미널에서
export CLIPSYNC_URL=http://localhost:8787
export CLIPSYNC_PASSPHRASE="abacus abdomen abide abnormal abrasion abroad absence"
```

**CLI 테스트 피어** (`protocol/ref`): 앱 없이 서버에 붙어 항목을 보내고 받습니다.

```sh
node src/cli.ts send "텍스트"                     # 텍스트 보내기 (send-html, send-image a.png, send-file a.pdf 도 있음)
node src/cli.ts list                              # 서버 항목을 복호화해 출력
node src/cli.ts watch                             # 실시간 이벤트 보기
node src/cli.ts config-get                        # vault 설정 보기
node src/cli.ts config-set images=on files=on max=20
```

배포 확인: `BASE_URL=https://… AUTH_TOKEN_HEX=… node server/scripts/smoke.mjs --big` (20 MiB 왕복 → 삭제 → 410, 51 MiB 초과 → 413).

## 문서

1. [요구사항](docs/00-requirements.md) — 무엇을 만드는가
2. [기술 명세](docs/01-tech-spec.md) — 아키텍처, 암호, API, 동작 규칙, 결정 로그
3. [구현 계획](docs/02-implementation-plan.md) — 마일스톤과 완료 기준
4. [핸드오프](docs/03-handoff.md) — 현재 상태와 다음 할 일
5. [남은 작업](docs/04-remaining.md) — 멈춘 지점, 마무리할 일, 미뤄 둔 검증, 알려진 한계
6. [스파이크 결과](docs/spikes.md) — 착수 전 검증
7. [프로토콜](protocol/PROTOCOL.md) — 바이트 포맷과 테스트 벡터 규칙

## 알려진 한계

- 기기 2대를 기준으로 설계했습니다. 3대 이상이면 먼저 적용한 기기가 서버 본문을 지워 나머지는 이미지·파일을 받지 못합니다.
- 개인 설치용입니다. 공증·앱스토어 배포를 하지 않으며, 자체 서명한 Mac 앱은 **다시 빌드할 때마다** Keychain 접근 확인 창이 한 번 뜹니다.
- 폴더 복사, 비밀번호 관리자 항목 자동 제외, 압축은 지원하지 않습니다.
- Windows 단일 실행 파일 배포(self-contained publish)는 M7에서 정리할 예정입니다.
