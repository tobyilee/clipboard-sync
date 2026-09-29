# Clipboard Sync — 구현 계획서

> 상태: 계획 확정 (구현 미착수)
> 작성일: 2026-09-29 / 개정: 2026-09-29 (advisor 리뷰 반영)
> 선행 문서: [00-requirements.md](./00-requirements.md), [01-tech-spec.md](./01-tech-spec.md)
> 원칙: 각 마일스톤은 **독립적으로 검증 가능한 완료 기준**을 가지며, 완료 기준을 통과해야 다음으로 넘어간다.

## 1. 전체 순서와 의존 관계

```
M0 준비/스파이크
   │
   ▼
M1 protocol (테스트 벡터)
   │
   ▼
M2 서버 ──────────────┬───────────────┐
                      ▼               ▼
              M3 Mac 텍스트     M4 Windows 텍스트     ← 병렬 가능
                      │               │
                      └───────┬───────┘
                              ▼
                  M5 vault 설정 + 이미지/파일
                              ▼
                  M6 수신 후 삭제 + 오프라인/재연결
                              ▼
                  M7 마감 (온보딩, 알림, 자동 시작, E2E)
```

- 1인 개발 기준 **순차 진행이 기본**이다. M3/M4는 서버(M2)가 끝나면 독립적이므로 순서를 바꾸거나 병행해도 된다.
- Mac 앱을 먼저 진행하는 것을 권장한다 (설정 UI가 Mac에만 있고, 개발 환경이 이미 Mac이므로).

## 2. M0 — 준비와 스파이크

목적: 설계의 **불확실한 가정 5가지**를 코드 몇십 줄 수준으로 먼저 검증해, 큰 구현 뒤에 설계가 무너지는 일을 막는다.

### 2.1 환경 준비
| 항목 | 내용 |
|---|---|
| Cloudflare | 계정, `wrangler` 로그인, R2 활성화(결제 수단 필요 여부 확인). **요금제는 Workers Free로 확정**(사용자 결정)하고 Free에서 R2 binding / SQLite 기반 DO / Rate Limiting binding이 모두 되는지 확인한다 (조사상 DO·Rate Limiting은 Free 가능, R2 binding은 미확인 → S-1로 검증) |
| Mac | Xcode(macOS 26 SDK 포함), 개발 기기는 macOS 26. **서명 ID 확정: `Apple Development: tobyilee@gmail.com (P7H3D7D535)`** (팀 ID `P7H3D7D535`, 이 Mac의 키체인에 유효한 상태로 존재 — `security find-identity -v -p codesigning`으로 확인). 프로젝트에 고정하며 ad-hoc 서명 금지. 최소 배포 타겟은 macOS 14 가정, 신규 pasteboard API는 `#available` 게이트 |
| Windows | **.NET 10 SDK (LTS)** — .NET 8/9는 2026-11-10 지원 종료. 클라우드 Windows(RDP 접속)에서 빌드·실행하며 GUI 데스크톱 세션이 필요. RDP 클립보드 리디렉션을 끌 수 있는 클라이언트 설정 확인 |
| 저장소 | `git init`, 모노레포 디렉터리(`protocol/ server/ mac/ windows/`), `.gitignore`, 루트 README (문서 링크) |

### 2.2 스파이크 (버리는 코드; 결과는 문서에 기록)
| # | 검증할 가정 | 방법 | 통과 기준 | 실패 시 |
|---|---|---|---|---|
| S-1 | Worker가 **20MB 본문을 R2로 스트리밍**할 수 있고 DO 메모리를 쓰지 않는다 (**Workers Free에서 실행**) | Worker에서 `PUT` 본문을 `R2.put(key, request.body)`로 저장 후 GET | 20MB, 50MB 업/다운로드 성공, 오류 없음 | 클라이언트가 R2 presigned URL로 직접 업로드하는 구조로 변경 (spec D-15 재검토) |
| S-2 | **macOS pasteboard 권한 동작**: `changeCount` 폴링은 프롬프트 없음, 내용 읽기는 프롬프트/`accessBehavior` | 최소 메뉴바 앱에서 `changeCount` 폴링 + 읽기 + `accessBehavior`/`detect*` 호출 | 실제 SDK 시그니처 확인, 허용 안내 경로 확정, **안정 서명 ID로 재빌드해도 pasteboard 허용·Keychain 접근·파일 접근(TCC)이 유지됨** | spec 7장 온보딩 설계를 실측에 맞게 수정 |
| S-3 | Windows `AddClipboardFormatListener` + **CF_HTML/PNG/CF_HDROP** 읽기·쓰기 | 작은 WinForms 앱 | 텍스트/HTML/PNG/파일이 Explorer, 브라우저, Office와 상호 붙여넣기 됨 | 표현 매핑표(spec 8장) 수정 |
| S-4 | Mac↔Win **PBKDF2/HKDF/AES-GCM 상호운용** | Swift와 C#에서 같은 입력으로 파생·암호화 | 같은 키/복호화 성공 | M1로 그대로 진행 (실패는 사실상 구현 버그) |
| S-5 | 클라우드 Windows(RDP)의 **클립보드 리디렉션 on/off 동작**: 리디렉션이 Mac↔VM 클립보드를 자체 동기화하는지, 커스텀 포맷(마커)이 통과하는지 | Mac의 RDP 클라이언트에서 리디렉션 off/on으로 텍스트·이미지·파일을 복사해 관찰 | off 상태에서 어떤 클립보드도 넘어가지 않음 확인 (이후 모든 Windows 테스트의 전제) | 리디렉션을 끌 수 없으면 Windows 테스트 환경 재검토 (로컬 VM 등) |

- 스파이크 결과는 `docs/spikes.md`에 한 줄씩 기록한다 (가정 / 결과 / 설계 변경 여부).
- S-1, S-2가 가장 위험하다. **S-1 실패 시 서버 구조가, S-2 실패 시 Mac 감시 방식이 바뀐다.**

**완료 기준:** 5개 스파이크의 결과가 기록되고, 설계 변경이 필요하면 01-tech-spec에 먼저 반영되었다.

## 3. M1 — protocol (테스트 벡터)

산출물
- `protocol/PROTOCOL.md`: 키 파생, 암호화 단위, AAD 레이아웃, 번들 바이트 포맷, config 포맷을 spec에서 뽑아 **구현의 단일 출처**로 정리
- `protocol/test-vectors.json`: passphrase → master → enc_key/auth_token/vault_id, 고정 nonce 기반 header/body/config 암호문, 번들 인코딩 바이트, 잘못된 AAD 복호화 실패 케이스
- `protocol/ref/`: **TypeScript 참조 구현** (의존성 0, Node 24 native type stripping) — 벡터 생성기 + **CLI 테스트 피어의 프로토콜 코어**(키 파생, seal/open, 번들, AAD). 피어의 **WebSocket/HTTP 네트워킹 부분은 서버가 생기는 M2로 옮긴다** (M1에는 붙일 상대가 없음). M2 서버 테스트와 M3/M4 앱 검증에서 재사용한다
- `mac/ClipSyncCore`(SwiftPM), `windows/ClipSync.Core`(+ 테스트 프로젝트, `net10.0`): 앱 프로젝트가 아닌 **프로토콜 라이브러리**. 같은 `test-vectors.json`을 읽는 테스트를 가진다 (Xcode 앱 프로젝트는 M3)
- 벡터 확정 전 결정(D-31 폴더 항목 무시, D-32 passphrase 공백 정규화, D-33 UUID 바이트 순서·번들 엄격 규칙)은 spec에 반영 완료

**완료 기준:** 참조 구현이 벡터를 만들고, Swift 패키지와 C# 라이브러리에서 **같은 벡터를 읽는 단위 테스트가 통과**한다 (Mac/Windows 상호운용을 서버·UI 이전에 확정).

## 4. M2 — 서버 (Worker + Durable Object + R2)

세부 작업 순서
1. 프로젝트 골격, `wrangler.toml`(DO, R2, secret `VAULT_ID`, rate limit binding). R2 lifecycle 1일은 버킷 설정이라 `wrangler r2 bucket lifecycle`로 별도 등록
2. 인증 미들웨어 (상수시간 비교, 401 rate limit, 51 MiB `Content-Length` 검사)
3. DO 스키마와 `PUT /body`(DO/R2 분기) → `POST /items` 커밋(seq, 20개 cap, broadcast)
4. `GET /items`, `GET /items/{id}/body`
5. WebSocket hibernation (`hello`, `item`, auto-response ping/pong)
6. alarm: 24h 만료, 고아 본문 정리 (10분)
7. 이 단계에서는 `DELETE /body`, `config` API도 함께 구현한다 (서버는 한 번에 끝내는 편이 단순).

**완료 기준**
- spec 10장의 서버 테스트 목록이 `vitest`로 모두 통과
- `wrangler dev`와 실제 배포 환경에서 `curl`/스크립트로 20MB 업로드→다운로드→삭제(410) 확인
- CLI 테스트 피어 두 개로 `item`/`body_purged`/`config` 이벤트 수신 확인

## 5. M3 — macOS 앱 (텍스트 우선)

세부 작업 순서
0. **Swift↔서버 상호운용(UI 전에)**: `ClipSyncCore`에 네트워킹 클라이언트를 만들고 `clipsync-dev`에서 양방향 교차 복호화(Swift 송신→`cli.ts` 수신, 반대도)를 opt-in 통합 테스트로 확인. Authorization 헤더가 URLSession(데이터/WebSocket)에서 실제로 전달되는지, id 소문자 hex, base64url, 텍스트 ping 확인
1. 메뉴바 앱 골격 (`LSUIElement`, `NSStatusItem`, SwiftUI 메뉴), 상태 모델
2. 온보딩: passphrase 생성/입력, 서버 URL, 키 파생, Keychain 저장, `vault_id` 표시
3. `changeCount` 감시 + 텍스트/HTML 읽기 (HTML이 없으면 RTF→HTML 변환) + 에코 마커 + 정규화 해시 dedupe
4. 송신 (암호화, PUT/POST), WebSocket 수신, 클립보드 적용
5. 메뉴: 전체 on/off, 일시정지, 보내기/받기, 최근 항목 20개(선택 시 복원)
6. 자동 시작 (`SMAppService`)

**완료 기준:** Mac 앱과 **CLI 테스트 피어**(`protocol/ref`) 사이에서 텍스트/HTML(RTF 소스 포함)이 양방향 동기화되고, 에코 루프가 없으며, 전체 off·일시정지·방향 토글이 동작한다. 앱을 재빌드한 뒤에도 권한 프롬프트가 다시 뜨지 않는다. (같은 Mac에서 앱 두 개를 띄우면 pasteboard를 공유해 검증이 되지 않으므로 그 방식은 쓰지 않는다.)

## 6. M4 — Windows 앱 (텍스트 우선)

세부 작업 순서
1. WinForms 트레이 앱 골격, `NotifyIcon` 메뉴
2. 온보딩(passphrase/서버 URL 입력), 키 파생, Credential Manager 저장
3. 클립보드 리스너 (STA, 재시도/백오프, debounce), 텍스트/HTML(`CF_HTML` 생성·파싱)
4. 송수신, 에코 마커(`RegisterClipboardFormat`), `CanUploadToCloudClipboard=0`
5. 메뉴: Mac과 동일한 토글·최근 항목, 자동 시작(`HKCU Run`)

**완료 기준:** **Mac ↔ 클라우드 Windows 간 텍스트/HTML 양방향 동기화**가 동작한다. RDP 클립보드 리디렉션을 **off**로 두고 검증한다 (켜져 있으면 RDP가 클립보드를 직접 동기화해 오통과함). 리디렉션을 **on**으로 켠 상태에서도 에코 루프(무한 재업로드)가 생기지 않는지 별도로 테스트한다. 이것이 프로젝트의 첫 번째 사용자 가치 검증 지점이다 (M3+M4 통합 데모).

## 7. M5 — vault 설정 + 이미지/파일

세부 작업 순서
1. config 모델/암호화/`PUT`(If-Match)·`GET`, 클라이언트 fail-closed 기본값 (Mac, Windows 공통)
2. Mac 메뉴: "동기화 대상"(이미지/파일), "미디어 최대 크기" → config PUT. Windows: 읽기 전용 표시
3. 송신 판별(spec 4.5), 크기 검사, 이미지(PNG; TIFF/DIB→PNG 변환)와 파일 번들 인코딩, 파일명·preview NFC 정규화, 대용량 스트리밍 업로드와 진행률
4. 수신 적용 (Mac: `NSPasteboardItem` + file URL, Windows: PNG+DIBV5, `CF_HDROP`), 파일명 sanitize
5. 로컬 캐시 폴더 구조 (정리 정책은 M6)

**완료 기준**
- 기본 상태(OFF)에서 이미지/파일 복사는 무시되고 텍스트만 동기화됨
- Mac에서 이미지·파일을 켜면 Windows에도 반영되어 양쪽에서 동작
- 20MB 초과 시 알림 후 미전송, 5/10/50MB 변경이 즉시 반영
- 파일이 꺼진 상태에서 Finder 파일 복사가 **파일명 텍스트로 새지 않음**
- Finder에서 복사한 한글 파일명이 Windows에서 자모 분리 없이 표시됨 (NFC 정규화)

## 8. M6 — 수신 후 삭제 + 오프라인/재연결

세부 작업 순서
1. 적용 성공 후 `DELETE /body` 호출 (이미지/파일 항목만), `body_purged` 이벤트 처리, 목록의 "서버에서 삭제됨" 표시
2. 로컬 캐시 (24h / 200 MiB LRU), 캐시에서의 복원 경로
3. catch-up (`since=last_seq`), "가장 최신 하나만 적용", 새 항목 도착 시 진행 중 다운로드 취소, **로컬 우선 규칙** (spec 6.3: 오프라인 중 사용자가 새로 복사했으면 원격 항목으로 덮어쓰지 않음), **네트워크 실패한 최신 로컬 항목 1개를 pending으로 보관·재전송** (spec 6.2)
4. 재연결(백오프+jitter), 슬립/웨이크 즉시 재연결, 최초 실행 시 과거 항목 자동 적용 금지

**완료 기준:** 서버 저장소에서 적용 완료한 이미지/파일 본문이 사라지고(R2 객체 삭제 확인), 오프라인이던 기기가 재접속하면 최신 항목만 클립보드에 적용되고 (단, 오프라인 중 그 기기에서 새로 복사한 내용이 있으면 덮어쓰지 않고 업로드 실패했던 그 항목이 먼저 전송되며), 삭제된 항목은 캐시가 있는 기기에서만 복원된다.

## 9. M7 — 마감

- macOS pasteboard 권한 온보딩과 "권한 필요" 상태 (S-2 결과 반영), TCC 안내
- 알림 (크기 초과, 연결 오류, 권한 필요), Windows toast AUMID
- passphrase 변경/키 교체 절차 문서화 (새 `VAULT_ID` 등록, 서버 데이터 폐기)
- 설치 문서 `docs/03-setup.md`: 서버 배포(`wrangler deploy`, R2, secret) → Mac 설치 → Windows 설치
- spec 10장의 **수동 E2E 매트릭스 전체 수행**, 결과를 `docs/e2e-results.md`에 기록
  - 추가 확인: RDP 리디렉션 off/on 각각, 한글 파일명(Finder NFD 원본), RTF 전용 소스 앱, 오프라인 중 로컬 복사 후 재접속(로컬 우선 규칙)

**완료 기준:** 새로 배포한 서버에 처음부터 문서만 보고 두 기기를 연결할 수 있고, E2E 매트릭스에서 실패 항목이 없거나 알려진 한계로 기록되어 있다.

## 10. 위험 목록과 대응

| 위험 | 영향 | 확인 시점 | 대응 |
|---|---|---|---|
| Worker→R2 대용량 스트리밍 제약 | 서버 구조 변경 | M0 S-1 | presigned URL 방식으로 전환 |
| macOS 26 pasteboard 프롬프트가 UX를 크게 해침 | Mac 자동 동기화 품질 | M0 S-2 | 온보딩 + 타입 사전 판별, 필요시 사용자 트리거 방식을 옵션으로 검토 |
| Windows 클립보드 잠금/포맷 차이 | 간헐적 실패 | M4 | 재시도/백오프, 포맷 우선순위 |
| Mac 파일 복사 표현 차이(파일 URL vs promise) | 파일 감지 실패 | M5 | 실제 Finder/앱별 샘플 수집 후 판별 규칙 보강 |
| 에코 루프 | 무한 업로드 | M3 | 마커 + 해시 이중 방어, 테스트 시나리오 필수 |
| R2 활성화에 결제 수단 필요 | 진행 지연 | M0 | 초기에 계정 확인 |
| 두 언어(Swift/C#) 구현 불일치 | 복호화 실패 | M1 | 공용 테스트 벡터를 CI/수동 테스트에 포함 |
| Mac 재빌드마다 서명 정체성 변경 → pasteboard/TCC/Keychain 권한 초기화 | 반복 프롬프트, 개발 속도 저하 | M0 S-2 | 안정 서명 ID 고정 (ad-hoc 금지) |
| RDP 클립보드 리디렉션이 테스트를 오염 / 에코 루프 | 오통과, 무한 업로드 | M0 S-5, M4 | 리디렉션 off 기본, 정규화 해시 dedupe 필수, 별도 에코 루프 테스트 |
| 오프라인 중 복사한 내용이 재접속 시 원격 항목에 덮어써짐 | 사용자 데이터 손실 | M6 | 로컬 우선 규칙 + pending (사용자 승인 완료) |
| .NET 8/9 지원 종료 (2026-11-10) | 보안 패치 중단 | M0 | .NET 10 LTS 사용 |

## 11. 작업 방식

- **브랜치/커밋**: 마일스톤 단위로 작업하고 완료 기준 통과 시점에 커밋 (커밋 메시지에 `M<번호>` 접두).
- **문서 동기화**: 구현 중 spec을 바꿔야 하면 코드보다 **spec을 먼저 수정**하고 결정 로그(D-번호)에 추가한다.
- **범위 통제**: v1 범위 밖 항목(비밀번호 관리자 제외, 압축, 3대 이상 기기 등)은 이슈로만 기록하고 구현하지 않는다.
- **서버 리소스**: 개발 중에는 별도 Workers 이름(`clipsync-dev`)과 별도 R2 버킷을 사용하고, 운영 배포는 M7에서 한다.
- **Windows 테스트 환경 (클라우드 + RDP)**: RDP 클립보드 리디렉션은 앱과 무관하게 클립보드를 동기화하므로 기본은 **off**로 테스트하고, on 상태는 에코 루프 전용 테스트로만 쓴다. Windows 코드는 저장소를 clone해 클라우드 머신에서 빌드한다.
- **Mac 서명**: 모든 Mac 빌드는 M0에서 정한 동일한 서명 ID로 서명한다 (ad-hoc 서명 금지).

## 12. 시작 전 확인 사항

확정
1. R2 활성화·결제 수단 등록: **가능** (사용자 확인). 어느 요금제에서 R2 binding이 되는지는 S-1에서 실측.
2. Windows 개발/테스트: **사용자의 클라우드 Windows** (RDP 접속) — S-5와 리디렉션 off 테스트 원칙 적용.
3. Git: 완료 — private 저장소 `tobyilee/clipboard-sync`.
4. Mac 서명 ID: **Apple Development (`P7H3D7D535`)로 확정**. 자체 서명 인증서(`ClipSync Dev`)는 대안으로 키체인에 남아 있으나 사용하지 않는다.
5. Cloudflare 요금제: **Workers Free로 확정**. R2 binding이 Free에서 가능한지는 S-1에서 실측하고, 불가하면 대안(R2 S3 호환 API presigned URL 직접 업로드 등)을 검토한다. Free의 일일 한도는 M2에서 실측한다.
6. 00 FR-6 **로컬 우선 규칙: 승인됨** (2026-09-29).

M0 시작 전 미결 사항은 없다.
