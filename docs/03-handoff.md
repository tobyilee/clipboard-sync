# Clipboard Sync — 핸드오프 문서

> 작성일: 2026-09-29
> 목적: 새 세션/새 작업자가 이 문서와 `docs/00~02`만 읽고 **M0부터 바로 이어갈 수 있게** 현재 상태를 정리한다.
> 상태 요약: **설계·계획 완료, 코드는 아직 한 줄도 없음.** 다음 작업은 M0(준비와 스파이크).

## 1. 한눈에 보기

| 항목 | 내용 |
|---|---|
| 무엇을 | Mac ↔ Windows 양방향 클립보드 자동 동기화 (텍스트/HTML 기본, 이미지·파일은 옵션) |
| 구성 | macOS 메뉴바 앱(Swift) + Windows 트레이 앱(.NET 10) + Cloudflare 서버(Workers + Durable Object + R2), E2EE |
| 저장소 | https://github.com/tobyilee/clipboard-sync (private), 브랜치 `main` |
| 최신 커밋 | `8e516ff` docs: apply advisor review and finalize M0 decisions |
| 저장소 내용 | `docs/00-requirements.md`, `docs/01-tech-spec.md`, `docs/02-implementation-plan.md`, `docs/03-handoff.md`(이 문서), `.gitignore` |
| 다음 작업 | **M0** — 환경 준비 + 스파이크 S-1~S-5 (계획서 §2) |

## 2. 문서 읽는 순서
1. `00-requirements.md` — 무엇을 만들지 (FR/NFR 번호)
2. `01-tech-spec.md` — 어떻게 만들지 (아키텍처, 암호, API, 클라이언트 동작, **결정 로그 D-1~D-28**)
3. `02-implementation-plan.md` — 어떤 순서로 (M0~M7, 완료 기준, 스파이크, 위험)
4. 이 문서 — 지금 어디까지 왔고, 무엇이 확정/미확정인지

## 3. 확정된 핵심 결정 (사용자 승인 포함)
- **동기화:** 자동, 양방향. 텍스트(+HTML)는 항상 켜짐. **이미지·파일은 기본 OFF**, Mac 메뉴바에서만 on/off와 최대 크기(5/10/**20**/50MB) 편집. 설정은 vault 전체 설정으로 서버에 **암호화 저장**(서버는 값을 못 읽음), 못 받았으면 텍스트만(fail-closed).
- **크기:** 텍스트 항목 1 MiB, 이미지/파일 항목 기본 20MB. 서버 하드 캡 51 MiB.
- **수신 후 삭제:** 수신 기기가 클립보드 적용을 마치면 이미지/파일 **본문만** 서버에서 삭제(`DELETE /v1/items/{id}/body`), header는 24h 유지. 실제 Cmd/Ctrl+V는 서버가 알 수 없어 "적용 완료"를 기준으로 삼는다.
- **서버:** vault당 Durable Object 1개(SQLite, WebSocket hibernation), 1.25 MiB 초과 본문은 R2. 업로드는 `PUT body` → `POST` 커밋 2단계. 최근 20개·24h 보관.
- **암호:** passphrase → PBKDF2-SHA256(600k) → HKDF → AES-256-GCM. 첫 기기가 diceware 7단어 passphrase 생성(고정 salt의 안전성 전제). 서버 인증은 `VAULT_ID` secret allowlist.
- **로컬 우선 규칙(승인됨):** 오프라인/일시정지 중 사용자가 새로 복사한 내용은 재접속 시 원격 항목으로 덮어쓰지 않는다. 네트워크 실패한 최신 로컬 항목 1개는 pending으로 보관·재전송.
- **에코 방지:** private 마커 + **정규화 해시 dedupe(필수)**. RDP 클립보드 리디렉션이 마커를 못 전달하므로 해시가 실질 방어선.
- **플랫폼:** macOS(Swift, 비샌드박스, 최소 배포 타겟 macOS 14 가정), Windows(**.NET 10 LTS**, WinForms NotifyIcon). .NET 8/9는 2026-11-10 지원 종료라 사용하지 않는다.
- **Cloudflare:** **Workers Free** 사용(사용자 결정). R2 활성화·결제 수단 등록은 가능하다고 확인.
- **Mac 서명:** **`Apple Development: tobyilee@gmail.com (P7H3D7D535)`** 로 고정(ad-hoc 금지). 공증·앱스토어는 안 함.
- **테스트 환경:** Mac(로컬) + **클라우드 Windows(RDP 접속)**. RDP 클립보드 리디렉션은 기본 **off**로 테스트.

## 4. 이 세션의 환경 상태 (사실 확인 완료)
- `gh` 로그인: 계정 `tobyilee` (scope: repo, workflow 등)
- 이 Mac의 유효한 서명 ID 2개: `Apple Development: … (P7H3D7D535)` **(사용 확정)**, `ClipSync Dev` (자체 서명, 사용 안 함)
- `~/.clipsync-signing/` 폴더(저장소 밖)에 자체 서명 인증서 재료가 있다. **`cs.p12`(비밀번호 `tmp-pass`)는 삭제 권장**, 자체 서명 인증서 자체도 불필요하면 키체인에서 삭제 가능.
- **`wrangler` 로그인 안 됨.** M0 첫 단계에서 필요.
- Windows 클라우드 머신 접근, .NET 10 SDK 설치 여부는 **미확인**.
- 개발 Mac은 macOS 26(Darwin 25.6).

## 5. 다음에 할 일 (M0, 순서 제안)

1. **`! npx wrangler login`** — 브라우저 인증이 필요해 사용자가 직접 실행. (Claude가 대신할 수 없음)
2. **S-1 (최우선 위험):** Workers **Free**에서 R2 binding이 되는지, Worker가 20MB/50MB 본문을 R2로 스트리밍(`FixedLengthStream`, `R2.put(key, request.body)`)할 수 있는지, DO 메모리를 쓰지 않는지 확인.
   - 실패 시: R2 S3 호환 API presigned URL로 클라이언트가 직접 업로드하는 구조로 변경 (spec D-15 재검토, plan §2.2).
   - 텍스트 전용 동작은 R2와 무관하게 DO만으로 가능 → S-1 실패해도 텍스트 동기화는 진행 가능.
3. **S-2:** macOS pasteboard 권한 동작 (`changeCount` 폴링은 프롬프트 없음, 읽기는 프롬프트/`accessBehavior`/`detect*`), **Apple Development ID로 재빌드해도 권한·Keychain·TCC가 유지되는지**, `#available` 게이트 대상 API의 실제 SDK 버전.
4. **S-3:** Windows 클립보드 포맷(CF_HTML/PNG+DIBV5/CF_HDROP) 읽기·쓰기 (클라우드 Windows에서).
5. **S-4:** Swift ↔ C# PBKDF2/HKDF/AES-GCM 상호운용.
6. **S-5:** 클라우드 Windows RDP 클립보드 리디렉션 on/off 동작 (off에서 아무것도 안 넘어가야 함).
7. 스파이크 결과를 `docs/spikes.md`에 기록, 설계 변경이 필요하면 **spec을 먼저 수정**(결정 로그에 D-번호 추가).
8. M0 환경 준비 체크리스트(plan §2.1): `git` 모노레포 골격(`protocol/ server/ mac/ windows/`), Xcode 설정, .NET 10 SDK 설치.

이후 M1(protocol 벡터+TS 참조 구현) → M2(서버) → M3/M4(앱) → M5(설정+미디어) → M6(삭제·오프라인) → M7(마감). 각 완료 기준은 plan 참조.

## 6. 아직 검증되지 않은 가정 (웹 검색 요약 기반 — 구현 전 확인)
| 가정 | 확인 방법 |
|---|---|
| Workers Free에서 R2 binding 사용 가능 | S-1 |
| Workers Free의 DO/요청 일일 한도가 이 앱에 충분 | M2 실측 |
| macOS 26의 pasteboard 프라이버시 API 이름·시그니처·설정 경로 (`accessBehavior`, `detect*`) | S-2 (Xcode SDK 문서로 재확인) |
| Workers 요청 본문 한도(≥100MB)로 51 MiB 업로드 가능 | S-1 |
| DO SQLite 행/BLOB 상한 2MB | (조사로 확인, M2에서 재확인) |
| Windows 비패키지 앱의 토스트 알림 AUMID 등록 | M7 |
| `NSAttributedString` RTF→HTML 변환 품질 | M3 |

## 7. 알려진 함정 / 주의
- **`openssl`:** PATH의 Homebrew OpenSSL 3.6이 만든 `.p12`는 macOS `security import`가 개인 키를 못 읽는다. macOS 키체인용은 **`/usr/bin/openssl`(LibreSSL)** 을 쓴다.
- **RDP 클립보드 리디렉션:** 켜져 있으면 앱과 무관하게 Mac↔VM 클립보드가 동기화되어 테스트가 오통과하고, 마커 미전달로 에코 루프가 생길 수 있다. 기본 off, on은 에코 루프 전용 테스트.
- **같은 Mac에서 앱 두 개를 띄워 테스트하지 않는다.** pasteboard를 공유해 검증이 안 된다. `protocol/ref`의 **CLI 테스트 피어**를 쓴다.
- **파일이 꺼진 상태의 Finder 복사:** 파일명 텍스트로 폴백하지 않고 항목 전체를 무시(D-18).
- **Finder 한글 파일명(NFD):** 송신 시 NFC 정규화(D-25), E2E에 반드시 포함.
- **3대 이상 기기:** 처음 적용한 기기가 본문을 삭제하므로 나머지는 못 받음(v1 한계). 검증 대상은 2대.
- **서버 롤백(오래된 config 재전송)** 은 완전히 막지 못함 — 개인용 위협모델에서 수용.

## 8. 작업 방식 / 사용자 선호
- 문서는 **한국어**, 대화도 한국어. 사용자 전역 지침에 따라 매 프롬프트를 **영어로 다듬어 먼저 보여주고**(영어면 문법 교정) 진행한다.
- Git 단축어(사용자 전역 지침): `cm` 커밋, `p` push, `cmp` 커밋+push, `cmpr` 커밋+push+PR, `pr`, `mprm`, `mtm`, `ghelp`. 커밋 메시지 끝에 `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`, PR 본문 끝에 `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- 마일스톤 단위로 커밋, 메시지에 `M<번호>` 접두. spec을 바꿔야 하면 **코드보다 문서를 먼저**.
- v1 범위 밖(비밀번호 관리자 항목 제외, 압축, 3대 이상 기기, 다운스케일 등)은 이슈로만 기록.
- 개발 중 서버는 별도 이름(`clipsync-dev`)·별도 R2 버킷 사용, 운영 배포는 M7.
- 큰 결정은 advisor 검토를 받는다. (이 세션의 **첫 advisor 리뷰는 spec/plan 작성 후에야 이루어졌다** — D-1~D-20은 Claude 단독 판단이었고 리뷰 후 D-21~D-28이 추가·수정됨. 이후 큰 설계 변경은 리뷰를 먼저 받는다.)

## 9. 알려진 미결 / v1 이후
- 비밀번호 관리자 항목 자동 제외
- passphrase 분실/변경 절차 세부 (새 `VAULT_ID` 등록 → 서버 데이터 폐기)
- 알림 세부 사양 (macOS UNUserNotificationCenter / Windows toast)
- 대형 스크린샷 다운스케일 옵션 (현재는 20MB 상향으로 대체, D-14 폐기됨)
- 압축 도입 시 raw deflate로 통일

## 10. 새 세션 시작 체크리스트
1. `git pull` 후 `docs/00~03`을 순서대로 읽는다.
2. `security find-identity -v -p codesigning`으로 서명 ID(`P7H3D7D535`) 확인, `gh auth status` 확인.
3. `npx wrangler whoami`로 Cloudflare 로그인 상태 확인 (안 되어 있으면 사용자에게 `! npx wrangler login` 요청).
4. M0 §5의 1번부터 진행하고, 스파이크 결과를 `docs/spikes.md`에 남긴다.
