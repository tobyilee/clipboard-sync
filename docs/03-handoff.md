# Clipboard Sync — 핸드오프 문서

> 작성일: 2026-09-29 (M0 진행 중 갱신, 이전 버전을 대체)
> 목적: 새 세션/새 작업자가 이 문서와 `docs/00~02`, `docs/spikes.md`만 읽고 **M0의 남은 작업부터 바로 이어갈 수 있게** 현재 상태를 정리한다.
> 상태 요약: **설계·계획 완료. M0 완료(S-1~S-4 통과, S-5 부분). M1 완료(TS/Swift/C#이 같은 벡터 통과).** 제품 코드는 아직 없음(저장소는 문서 + 빈 디렉터리 골격).

## 1. 한눈에 보기

| 항목 | 내용 |
|---|---|
| 무엇을 | Mac ↔ Windows 양방향 클립보드 자동 동기화 (텍스트/HTML 기본, 이미지·파일은 옵션) |
| 구성 | macOS 메뉴바 앱(Swift) + Windows 트레이 앱(.NET 10) + Cloudflare 서버(Workers + Durable Object + R2), E2EE |
| 저장소 | https://github.com/tobyilee/clipboard-sync (private), 브랜치 `main` |
| 저장소 내용 | `docs/00~03`, `docs/spikes.md`, `README.md`, `protocol/ref/ server/ mac/ windows/`(현재 `.gitkeep`만), `.gitignore` |
| 다음 작업 | **M5 vault 설정 + 이미지/파일** |

## 2. 문서 읽는 순서
1. `00-requirements.md` — 무엇을 만들지 (FR/NFR 번호)
2. `01-tech-spec.md` — 어떻게 만들지 (아키텍처, 암호, API, 클라이언트 동작, **결정 로그 D-1~D-49**)
3. `02-implementation-plan.md` — 어떤 순서로 (M0~M7, 완료 기준, 스파이크, 위험)
4. `spikes.md` — 스파이크별 결과 (가정 / 결과 / 설계 변경)
5. 이 문서 — 지금 어디까지 왔고, 무엇이 확정/미확정인지

## 3. 확정된 핵심 결정 (사용자 승인 포함)
- **동기화:** 자동, 양방향. 텍스트(+HTML)는 항상 켜짐. **이미지·파일은 기본 OFF**, Mac 메뉴바에서만 on/off와 최대 크기(5/10/**20**/50MB) 편집. 설정은 vault 전체 설정으로 서버에 **암호화 저장**, 못 받았으면 텍스트만(fail-closed).
- **크기:** 텍스트 항목 1 MiB, 이미지/파일 항목 기본 20MB. 서버 하드 캡 51 MiB.
- **수신 후 삭제:** 수신 기기가 클립보드 적용을 마치면 이미지/파일 **본문만** 서버에서 삭제(`DELETE /v1/items/{id}/body`), header는 24h 유지.
- **서버:** vault당 Durable Object 1개(SQLite, WebSocket hibernation), 1.25 MiB 초과 본문은 R2. 업로드는 `PUT body` → `POST` 커밋 2단계. 최근 20개·24h 보관.
- **암호:** passphrase → PBKDF2-SHA256(600k) → HKDF → AES-256-GCM. 첫 기기가 diceware 7단어 passphrase 생성. 서버 인증은 `VAULT_ID` secret allowlist.
- **로컬 우선 규칙(승인됨):** 오프라인/일시정지 중 새로 복사한 내용은 재접속 시 원격 항목으로 덮어쓰지 않는다. 실패한 최신 로컬 항목 1개는 pending으로 재전송.
- **에코 방지:** private 마커 + **정규화 해시 dedupe(필수)**. RDP 클립보드 리디렉션이 마커를 못 전달하므로 해시가 실질 방어선.
- **플랫폼:** macOS(Swift, 비샌드박스, 최소 배포 타겟 macOS 14), Windows(**.NET 10 LTS**, WinForms NotifyIcon).
- **Cloudflare:** **Workers Free** 사용(사용자 결정).
- **Mac 서명:** **`Apple Development: tobyilee@gmail.com (P7H3D7D535)`** 로 고정(ad-hoc 금지). 공증·앱스토어는 안 함.
- **테스트 환경:** Mac(로컬) + **Windows 365 Enterprise Cloud PC(Mac의 Windows App으로 RDP 접속)**. RDP 클립보드 리디렉션은 기본 **off**로 테스트.
- **(신규) D-29:** pasteboard 신규 API 게이트는 `#available(macOS 15.4, *)`, 종류 판별은 `pasteboard.types`, 권한 온보딩은 `accessBehavior` 런타임 상태 기반.

## 4. 스파이크 진행 상황 (상세는 `docs/spikes.md`)

| # | 상태 | 요약 |
|---|---|---|
| S-1 | ✅ 통과 | Workers **Free**에서 R2 binding 동작. `R2.put(key, request.body)` 스트리밍으로 20/50/51 MiB 업·다운로드·삭제 성공, 바이트 일치. 설계 변경 없음(D-15 유지). 스파이크 Worker와 버킷은 삭제함 |
| S-2 | ✅ 통과 (경고 재현은 미확인) | `accessBehavior`/`detect*`는 macOS **15.4+**(이전 문서의 "26"은 오류). `detect*`는 종류 판별용이 아님 → `types`로 판별. Apple Development ID로 재빌드(코드가 다른 4개 빌드)해도 Keychain·TCC 허용 유지. macOS 26.6.2에서 새 앱이 첫 실행부터 `.alwaysAllow`라 경고 자체는 재현하지 못함 |
| S-3 | ✅ 통과 (합성 데이터) | 자가 테스트 전 항목 PASS. 합성 `CF_DIB`는 알파 손실 → DIBV5 우선. 실제 소스(Edge/Explorer/Office) 덤프는 미수집 → M3/M4에서 재검증. 코드·결과는 `spike/s3` 브랜치 |
| S-4 | ✅ 통과 | Swift ↔ C# PBKDF2/HKDF/AES-GCM 상호운용. 양쪽이 공용 벡터 35개 항목 전부 통과. D-30(HKDF 빈 salt 등) 추가. 벡터는 `protocol/test-vectors.seed.json`, C# 스파이크 코드는 `spike/s4` 브랜치 |
| S-5 | 🔶 부분 | 리디렉션 off 시 텍스트가 전달되지 않음 확인. on 상태 에코 루프·텍스트 외 형식은 미확인 |

M0 완료 기준(plan §2.2): 5개 스파이크 결과가 `docs/spikes.md`에 기록되고, 설계 변경이 필요하면 spec에 먼저 반영.

## 5. 환경 상태 (2026-09-29 확인)

| 항목 | 상태 |
|---|---|
| 개발 Mac | macOS 26.6.2, Xcode 27.0(SDK MacOSX27.0), Swift 6.4, Node 24, npm 11 |
| `xcode-select` | ✅ Xcode.app로 전환됨 |
| `gh` | ✅ 계정 `tobyilee` |
| 서명 ID | ✅ `Apple Development: tobyilee@gmail.com (P7H3D7D535)` (유효 ID는 이것 하나) |
| Cloudflare | ✅ wrangler 4.143.0, **`toby@epril.com`** 계정(Account ID `8121708927a91d4bab87da8b49fde255`), R2 활성화 완료, workers.dev 서브도메인 **`clipboardsync`** 등록 (서버 URL 형식 `https://<worker>.clipboardsync.workers.dev`). 현재 R2 버킷·Worker 없음(스파이크 리소스 삭제) |
| .NET SDK | Cloud PC: 10.0.401 설치됨. **이 Mac: M1부터 `ClipSync.Core` 테스트를 Mac에서 돌리므로 필요**(sudo 불필요): `curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh && bash dotnet-install.sh --channel 10.0 --install-dir ~/.dotnet` 후 `export PATH=~/.dotnet:$PATH`. (세션 scratchpad에 설치한 사본은 세션이 끝나면 사라진다) |
| Windows | **Windows 365 Enterprise Cloud PC**, Mac Windows App으로 접속, 관리자 권한 있음. 인바운드 SSH는 불가(Microsoft 관리 네트워크) → 작업은 사용자가 RDP에서 실행하고 결과를 전달 |

### Cloud PC 준비 (완료: .NET 10.0.401, git clone, RDP 클립보드 리디렉션 off. 재현용으로 남김)
```powershell
winget install Microsoft.DotNet.SDK.10
winget install Git.Git
winget install GitHub.cli
gh auth login
git clone https://github.com/tobyilee/clipboard-sync
dotnet --version   # 10.x
```
RDP 클립보드 리디렉션 끄기: Windows App의 Cloud PC 설정에서 Clipboard 해제 + (필요 시) Intune 정책 확인. **끈 뒤 Mac에서 복사 → Windows 메모장에 붙여 아무것도 안 붙는지 확인**하는 것이 S-5 자체다.

## 6. 다음에 할 일 (순서)
0. **M0 완료, M1 완료(Mac과 Cloud PC 모두에서 통과 확인, 2026-09-29).** `protocol/PROTOCOL.md`, `protocol/test-vectors.json`(생성물, 직접 수정 금지), `protocol/ref`(TS, `npm run gen` / `npm test`), `mac/ClipSyncCore`(`swift test`), `windows/ClipSync.Core`(+ Tests, `dotnet test windows/ClipSync.sln`)가 같은 벡터를 통과한다. 결정 D-30~D-33 반영.
   - Cloud PC `dotnet test windows\ClipSync.sln` 9개 통과. 처음에는 NuGet 소스 문제(`NU1100`)로 복원이 실패했고 재시도로 해결됨(원인 미확인, 부동 버전은 고정함).
   - `swift test`가 `TestingMacros plugin not found`로 실패하면 `rm -rf mac/ClipSyncCore/.build` 후 재실행(다른 툴체인이 만든 캐시 위 증분 빌드에서 간헐 발생, 콜드 빌드는 CLT/Xcode 모두 통과).
1. **M2 서버: 구현·테스트·개발 배포 완료(미커밋).** `server/`(Worker+DO+R2), `vitest` 22개 통과, 로컬 `wrangler dev`와 배포(`https://clipsync-dev.clipboardsync.workers.dev`, 버킷 `clipsync-dev-bodies` + 1일 lifecycle, secret `VAULT_ID`=테스트 벡터 값)에서 `server/scripts/smoke.mjs --big`(20 MiB 왕복→삭제→410, 409, 413) 통과. D-34~D-37 반영. **CLI 테스트 피어 네트워킹 완료**(`protocol/ref/src/peer.ts`, `cli.ts`: Node 24 내장 fetch/WebSocket만 사용, WebSocket은 두 번째 인자 `{headers}`로 Authorization 전달 확인). 서버 통합 테스트는 `CLIPSYNC_URL`/`CLIPSYNC_PASSPHRASE`가 있을 때만 실행되며(예: 개발 배포 URL + 테스트 벡터 passphrase `abacus abdomen abide abnormal abrasion abroad absence`) 실제 암호화 왕복(텍스트 inline, 2 MiB R2, 수신 삭제, 401)이 통과했다. 배포 환경에서 429 확인(병렬 60건 중 35건; 순차 16건은 429가 안 걸림: Rate Limiting binding 카운터는 위치별·최종 일관성이라 순차 소량 실패는 통과할 수 있음, 방어는 확률적). **남은 것:** Workers Free 일일 한도 실측(요청·DO 실행시간은 M3~M4에서 실제 사용 패턴으로 확인). 프로덕션 배포는 M7.
1a. **M3 macOS 앱: 구현·자동 검증 완료(미커밋 포함), 수동 확인 대기.** `mac/ClipSyncApp`(SwiftPM 실행 타깃) + `mac/scripts/build-app.sh`(번들 조립·서명·`~/Applications` 설치, 지정 요구사항이 인증서 기반인지 검사). `ClipSyncCore`에 `ServerClient`(prepare/upload로 재시도 시 같은 id), `PassphraseGenerator`(EFF 7단어, 거절 샘플링), `SyncLogic`(해시 정규화·dedupe 링, HTML fragment, catch-up 계획), `RTFConversion`, `ServerURLInput`. Swift 테스트 36개 통과(서버 교차·하트비트 타임아웃 테스트는 `CLIPSYNC_URL`/`CLIPSYNC_PASSPHRASE`가 있을 때만). 앱: 온보딩(연결 테스트 후 Keychain 저장), 메뉴(연결 상태, 동기화/보내기/받기, 일시정지, 최근 항목 20개, 로그인 시 자동 시작), `SyncEngine`(250ms 폴링 → 송신, WebSocket → 적용, 지수 백오프 재연결, 슬립/웨이크 재연결).
   - **자동 검증 결과(실앱 ↔ `clipsync-dev` ↔ CLI 피어):** CLI→Mac 텍스트, Mac→CLI 텍스트, CLI HTML→Mac(`<meta charset>` 추가, 한글·이모지 유지), Mac RTF만 → HTML fragment(style 유지, 한글 정상) 전송, 에코 없음(적용 후 재업로드 없음), 보내기 off/받기 off/일시정지 동작, 재개 후 업로드. Keychain 저장·조회·삭제가 서명 번들에서 프롬프트 없이 통과(`ClipSync --selftest-keychain`).
   - **사용자 수동 확인 완료(2026-09-29):** 재빌드 후 Keychain·클립보드 프롬프트 재발 없음, 동기화 off/on(아이콘·송수신 중지·재연결), 로그인 시 자동 시작, 최근 항목 복원 모두 문제 없음.
   - **하트비트(D-28)**: 텍스트 ping 30초, 90초간 메시지가 없으면 소켓을 끊고 재연결(`events(pingInterval:pongTimeout:pingPayload:)`, 서버 상대 통합 테스트로 타임아웃 경로와 정상 ping 대조군 확인). 상태 워터마크는 `lastSeq`(목록)와 `lastAppliedSeq`(적용)로 분리해 저장한다. **현재 동작(spec 완료 아님):** 일시정지·받기 off 중 도착한 항목은 `lastSeq`가 넘어가 해제 후에도 적용되지 않으며, 전체 off→on/앱 재시작 후에는 catch-up이 최신 항목을 무조건 적용한다(로컬 우선 규칙 미구현, M6).
   - `--selftest-keychain`은 실제 항목(account `passphrase`)과 분리된 account `selftest`를 쓴다. RTF/HTML만 있는 복사에도 plain text fallback을 함께 보낸다(kinds `["text","html"]`).
   - **미구현(이후 마일스톤):** 알림(spec 6.6, 지금은 메뉴에 메시지 표시), 로컬 우선·pending(M6), 이미지/파일·vault 설정(M5), 일시정지/off 해제 직후 catch-up 재실행(M6).
   - **정정:** 인증서 CN의 `P7H3D7D535`는 사용자 ID이고 실제 TeamIdentifier는 `4T2Y2T7SHU`.
1b. **M4 Windows 앱: 완료(텍스트 기준, 사용자 결정 2026-09-29).** S-5 방향별 확인(양방향 off) 후 착수. D-44~D-49 반영.
   - **Mac에 .NET SDK 10.0.401 설치(`~/.dotnet`, 사용자 승인)** → `PATH="$HOME/.dotnet:$PATH" dotnet test windows/ClipSync.sln`로 Core 테스트와 서버 교차 테스트(`CLIPSYNC_URL`/`CLIPSYNC_PASSPHRASE` 설정 시), `dotnet build`로 WinForms 앱 컴파일(`EnableWindowsTargeting`)을 Mac에서 먼저 확인한다. `windows/global.json`이 10.0.401(latestPatch)로 고정.
   - `ClipSync.Core`(net10.0): `ServerClient`(HttpClient + ClientWebSocket, 텍스트 ping/90초 타임아웃, 프레임 조립, `purged` 선택), `SyncLogic`(해시·dedupe 링·catch-up·LF→CRLF·HTML→plain), `CfHtml`(Build/ExtractFragment, 오프셋→주석→body 폴백), `ServerUrlInput`. C# 테스트 40개 통과(Mac, 서버 교차 포함: C#↔CLI 양방향, WS inline→삭제→`body_purged`, pong 타임아웃, 401).
   - `ClipSync.App`(net10.0-windows WinForms, 실행 파일 `ClipSync.exe`): 메시지 전용 윈도우 클립보드 리스너(100ms debounce), raw Win32 읽기/쓰기(마커 `ClipSyncItemId`, `CanUploadToCloudClipboard=0`, 시퀀스 번호로 자기 쓰기 건너뜀), Credential Manager(`ClipSync/passphrase`, `--selftest-credential`은 `ClipSync/selftest`), `%LOCALAPPDATA%\ClipboardSync\{settings.json,logs\}`, 트레이 메뉴(Mac과 동일 + "로그 폴더 열기"), balloon 알림, `HKCU Run` 자동 시작, named mutex 단일 실행, 전원 복귀 시 재연결.
   - D-49(같은 내용이면 적용 생략)는 Mac 앱에도 반영하고 실측 확인(같은 내용 → `changeCount` 불변, 다른 내용 → 마커와 함께 기록).
   - **Cloud PC 실측(2026-09-29):** `dotnet test` 40개 통과, `--selftest` 11개 항목 ALL PASS(Credential, 쓰기/읽기, 시퀀스 번호, 리스너, 마커, CRLF, CF_HTML 한글·이모지 오프셋, D-49 해시, 서버 조회, WebSocket hello). **Mac→Windows 텍스트**(한글·이모지·두 줄 → 메모장에 CRLF로 붙음), **Windows→Mac 텍스트** 모두 동작, 서로 적용 후 재업로드(에코) 없음(서버 기록 51·52 두 건뿐). Mac→서버 리치 텍스트(RTF→`text`+`html`)도 업로드 확인.
   - **버그(2026-09-29, 수정):** 첫 Windows→Mac 적용(seq 52) 뒤 Mac 앱이 원격 항목을 더 처리하지 못하고 재연결도 하지 않았다(`lastSeq` 52 고정, 보내기는 정상). 유력한 원인: 연결 감시 루프가 ping 전송(`URLSessionWebSocketTask.send`) 완료를 `await`해, 연결이 조용히 끊기면 감시도 멈춤. 수정: Swift는 완료 핸들러 형태로 보내고 기다리지 않음, C#은 ping 전송에 10초 시한. Mac 앱에 파일 로그 추가(`~/Library/Logs/ClipSync/clipsync.log`, 내용 미기록). 수정 후 원격 항목 7개/3.5분 모두 0.5초 내 적용. **주의:** 이 Mac에서는 `lsof`가 앱 소켓을 보여 주지 않는다 → 연결 확인은 `netstat -anv -p tcp | grep ClipSync`. 원인은 재현으로 확정하지 못했으므로 다시 생기면 로그로 확인한다.
   - **사용자 결정으로 미룬 검증(M7 수동 E2E 매트릭스에서):** Windows 쪽 HTML 붙여넣기 확인(Edge contenteditable 등), Edge에서 복사한 CF_HTML → Mac 적용, RDP 리디렉션 **on** 에코 루프 테스트(통과 기준: 복사 1회당 서버 항목 ≤2, 이후 90초간 추가 없음; 테스트 후 리디렉션 off 복귀), Windows 자동 시작(`HKCU Run`)은 개발 빌드 경로라 미설정.
   - Cloud PC에서 Mac으로 출력 전달은 **Mac 스크린샷**(Windows App 창, 바탕화면 저장)을 에이전트가 직접 읽는 방식이 동작했다. 파일명에 U+202F가 있어 스크래치 폴더로 복사 후 읽는다.
2. S-5의 on 상태(에코 루프)·방향별 확인은 미완이므로 **M4(Windows 텍스트 클라이언트) 전에** 재확인 (한쪽 방향 리디렉션이 켜져 있으면 M4 텍스트 테스트가 오통과한다).
3. 스파이크 브랜치 `spike/s3`, `spike/s4`는 원격에 남아 있다(버리는 코드; `spike/s3`의 `out/`은 M3/M4 fixture 후보). 필요 없어지면 삭제.
4. M3/M4에서 실제 소스(Edge, Explorer 복사/잘라내기/폴더, Snipping Tool, Office) 덤프로 CF_HTML 파서·DIB→PNG·HDROP 처리를 재검증한다. 폴더 포함 복사는 D-31(항목 전체 무시 + 알림).

## 7. 알려진 함정 / 주의 (스파이크에서 배운 것 포함)
- **Mac 권한 테스트는 CLI가 아니라 서명된 `.app`으로.** pasteboard/TCC는 "책임 프로세스"에 귀속돼 셸에서 실행한 바이너리의 결과는 무효다. 번들을 만들고 `codesign` 후 실행하며, **TCC 검증은 Finder 더블클릭으로 직접 실행**(Claude의 `open`은 권한이 Claude 쪽에 귀속될 수 있음).
- **신규 `*.workers.dev` 서브도메인은 TLS 인증서 준비까지 수 분이 걸린다.** 그동안 curl이 TLS 핸드셰이크 오류(35)로 실패한다. 서브도메인 등록은 wrangler가 자동 선택한 이름이 겹치면 실패하므로 API(`PUT /accounts/<id>/workers/subdomain`)로 직접 지정.
- **`detectMetadata` Swift 이름:** `NS_REFINED_FOR_SWIFT`라 `pb.__detectMetadata(forTypes:completionHandler:)`, 상수는 `__NSPasteboardMetadataType.__contentType`. 파일 복사에서도 오류(-10)를 반환했고 원인은 미규명 — 지금은 사용하지 않는다(types로 판별).
- **테스트용 다중 파일 복사:** AppleScript `set the clipboard to {POSIX file …, POSIX file …}`는 실제 Finder 복사와 다른 리스트 표현을 만든다. 앱 안에서 `NSPasteboard.writeObjects([NSURL…])`을 쓰거나 Finder에서 직접 복사.
- **`.p12`/`openssl`:** PATH의 Homebrew OpenSSL 3.6이 만든 `.p12`는 macOS `security import`가 못 읽는다. 키체인용은 `/usr/bin/openssl`(LibreSSL). (자체 서명 인증서 재료는 이미 정리됨)
- **RDP 클립보드 리디렉션:** 켜져 있으면 앱과 무관하게 클립보드가 동기화되어 테스트가 오통과하고 마커 미전달로 에코 루프가 생길 수 있다. 기본 off, on은 에코 루프 전용 테스트.
- **같은 Mac에서 앱 두 개를 띄워 테스트하지 않는다.** pasteboard를 공유한다. `protocol/ref`의 **CLI 테스트 피어**를 쓴다.
- **파일이 꺼진 상태의 Finder 복사:** 파일명 텍스트로 폴백하지 않고 항목 전체 무시(D-18). **Finder 한글 파일명(NFD)** 은 송신 시 NFC(D-25).
- **3대 이상 기기:** 처음 적용한 기기가 본문을 삭제하므로 나머지는 못 받음(v1 한계). **서버 롤백(오래된 config 재전송)** 은 개인용 위협모델에서 수용.
- 스파이크 코드는 세션 scratchpad에 있었고 저장소에 없다(버리는 코드). 필요하면 `docs/spikes.md`의 결과와 이 문서의 함정 목록으로 재현할 수 있다.

## 8. 아직 검증되지 않은 가정
| 가정 | 확인 시점 |
|---|---|
| Workers Free의 DO/요청 일일 한도가 이 앱에 충분 | M2 실측 |
| 51 MiB 초과 업로드가 `Content-Length` 검사로 413 거부됨 | M2 |
| DO SQLite 행/BLOB 상한 2MB | M2 |
| macOS 15.4~15.x 및 다른 기본값에서 `.ask` 경고 문구·설정 경로 | 해당 OS를 만났을 때(현재 환경은 `.alwaysAllow`) |
| Windows 비패키지 앱의 토스트 알림 AUMID 등록 | M7 |
| `NSAttributedString` RTF→HTML 변환 품질 | M3 |
| Windows 클립보드 포맷 상호 붙여넣기, RDP 리디렉션 off 효과 | S-3, S-5 |

## 9. 작업 방식 / 사용자 선호
- 문서는 **한국어**, 대화도 한국어. 사용자 전역 지침에 따라 매 프롬프트를 **영어로 다듬어 먼저 보여주고**(영어면 문법 교정) 진행한다.
- Git 단축어(사용자 전역 지침): `cm` 커밋, `p` push, `cmp` 커밋+push, `cmpr` 커밋+push+PR, `pr`, `mprm`, `mtm`, `ghelp`. 커밋 메시지 끝에 `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`, PR 본문 끝에 `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. `cm`/`cmp`/`cmpr`은 현재 브랜치에 커밋(브랜치 생성·전환 금지, `main`이어도).
- 마일스톤 단위로 커밋, 메시지에 `M<번호>` 접두. spec을 바꿔야 하면 **코드보다 문서를 먼저**.
- v1 범위 밖(비밀번호 관리자 항목 제외, 압축, 3대 이상 기기, 다운스케일 등)은 이슈로만 기록.
- 개발 중 서버는 별도 이름(`clipsync-dev`)·별도 R2 버킷 사용, 운영 배포는 M7.
- **큰 결정과 새 스파이크 착수 전에는 advisor(Opus 5.5) 검토를 받는다.** S-2에서 advisor가 "CLI가 아니라 서명된 .app으로 하라"는 핵심 함정을 미리 짚어 결과 오염을 막았다.
- 사용자만 할 수 있는 일(브라우저 로그인, `sudo`, Finder 더블클릭, RDP 안 작업)은 `! <명령>` 형태나 명확한 단계로 요청한다. 공개 이름 등록처럼 영구적인 선택(예: workers.dev 서브도메인)은 사용자에게 먼저 묻는다.

## 10. 알려진 미결 / v1 이후
- 비밀번호 관리자 항목 자동 제외
- passphrase 분실/변경 절차 세부 (새 `VAULT_ID` 등록 → 서버 데이터 폐기)
- 알림 세부 사양 (macOS UNUserNotificationCenter / Windows toast)
- 압축 도입 시 raw deflate로 통일

## 11. 새 세션 시작 체크리스트
1. `git pull` 후 `docs/00~03`, `docs/spikes.md`를 순서대로 읽는다.
2. `security find-identity -v -p codesigning`, `gh auth status`, `npx wrangler whoami`(`toby@epril.com`인지), `xcode-select -p`(Xcode.app인지) 확인.
3. 이 문서 §6의 1번(M0 마무리)부터 진행하고, 결과를 `docs/spikes.md`에 남긴다.
