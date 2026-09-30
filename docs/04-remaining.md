# 남은 작업 (Remaining)

> 기준일: 2026-09-30. 개발은 여기서 멈춤(사용자 결정). 다시 시작할 때는 이 문서 → [핸드오프](03-handoff.md) → [구현 계획](02-implementation-plan.md) §9(M7) 순서로 본다.
> 마지막 커밋: `cf8f62b` (M6 구현 + README).

## 0. 현재 상태 한눈에

| 마일스톤 | 상태 |
|---|---|
| M0 스파이크 · M1 protocol · M2 서버 | 완료 |
| M3 macOS 앱 · M4 Windows 앱 (텍스트) | 완료 |
| M5 vault 설정 + 이미지/파일 | 완료 (Mac·Windows 양방향 실측) |
| **M6 수신 후 삭제 + 오프라인/재연결** | **구현 완료, Mac 실측 완료, Windows 실측 미완** |
| M7 마감 | 미착수 |

## 1. 바로 이어서 할 일 (M6 마무리)

1. **Mac 앱의 "동기화"가 꺼져 있음.** 2026-09-30 14:46에 메뉴에서 꺼졌다(`defaults read com.tobylee.clipsync syncEnabled` = 0). 의도한 것이 아니면 메뉴바 ClipSync → **동기화**를 다시 켠다.
2. **Windows M6 빌드가 실제로 동작하는지 확인.** 마지막 확인 라운드에서 `git pull` + `windows\dev-run.cmd` 이후 Windows 기기(`c42f…`)가 올린 항목이 서버에 하나도 없었다(Win+Shift+S 캡처가 도착하지 않음). 먼저 확인할 것:
   - `dev-run.cmd`가 오류 없이 끝났는지, 트레이 아이콘이 초록(연결됨)인지
   - `%LOCALAPPDATA%\ClipboardSync\logs\clipsync.log`의 `engine start` / `connected` / `ERROR`
   - `ClipSync.exe --selftest` 결과
3. **M6 Windows 실측 (Mac 쪽은 로컬 서버로 확인 완료).** 개발 서버(`clipsync-dev`)에서:
   - Windows→Mac 이미지: Mac이 적용한 뒤 서버 본문이 `purged`가 되는지 (`protocol/ref`의 `node src/cli.ts list`)
   - Mac→Windows 이미지: **Mac pasteboard에 직접 올려서**(CLI로 보내면 제3 기기가 되어 먼저 적용한 쪽이 본문을 지움) Windows가 적용 후 삭제하는지
   - Windows 최근 항목의 "캐시에서 복원"
   - (선택) Windows 오프라인 중 복사 → 재연결 시 마지막 것만 전송 (pending)
4. **Mac "캐시에서 복원"을 메뉴로 한 번 눌러 보기.** 코드 경로(`restoreFromCache`)는 메뉴에서만 불린다. 서버에서 지워진 이미지·파일 항목에 "캐시에서 복원"이 붙는지, 누르면 클립보드로 돌아오는지.
5. `docs/03-handoff.md`의 M6 항목(1e)을 결과로 갱신하고 커밋.

## 2. M7 마감 (계획서 §9)

- **설치 문서** `docs/03-setup.md`: 새 Cloudflare 계정 기준 서버 배포(`wrangler deploy`, R2 버킷 + lifecycle, `VAULT_ID` secret) → Mac 설치(서명 인증서 만들기 포함) → Windows 설치. README의 설치 절을 기반으로.
- **프로덕션 배포**: 개발용 `clipsync-dev` / `clipsync-dev-bodies`와 **별개의 Worker·버킷**. **새 passphrase로 새 vault**를 만든다. 개발 vault는 테스트 벡터에 공개된 passphrase(`abacus abdomen …`)를 쓰므로 실제 사용 금지.
- **passphrase 변경/키 교체 절차** 문서화: 새 passphrase → 새 `VAULT_ID` 등록 → 서버 데이터 폐기 → 각 기기 재온보딩.
- **Windows 배포 형태**: self-contained 단일 파일 publish, 설치 위치 고정 후 `HKCU Run` 자동 시작 확인 (지금은 `bin\Release` 경로라 자동 시작을 켜지 않았음).
- **알림**: Mac은 `UNUserNotificationCenter` 사용 중이지만 이 Mac에서 권한이 "허용 안 됨"으로 기록됨 → 시스템 설정 → 알림 → ClipSync. Windows는 balloon(D-45); toast/AUMID는 필요할 때만.
- **macOS pasteboard 권한 온보딩**: `accessBehavior`가 `.ask`/`.alwaysDeny`일 때만 "권한 필요" 표시(구현됨, D-29). 15.4~15.x에서의 실제 동작은 미검증.
- **수동 E2E 매트릭스** (spec §10) 전체 수행 → `docs/e2e-results.md`. 특히 아래 "미뤄 둔 검증"을 포함.

## 3. 미뤄 둔 검증 (M4~M6에서 사용자 결정 또는 여건상)

| 항목 | 출처 | 비고 |
|---|---|---|
| Windows에서 HTML 붙여넣기 (Edge contenteditable 등) | M4 | Mac→서버 `text`+`html` 업로드와 Windows CF_HTML 오프셋은 selftest로 확인됨 |
| Edge에서 복사한 CF_HTML → Mac 적용 | M4 | |
| **RDP 클립보드 리디렉션 on 에코 루프 테스트** | M4, S-5 | 통과 기준: 복사 1회당 서버 항목 ≤2, 이후 90초간 추가 없음. 테스트 후 off로 복귀 |
| Windows 자동 시작(`HKCU Run`) | M4 | 배포 경로가 정해진 뒤 (M7) |
| Mac 메뉴에서 설정 토글(이미지·파일, 최대 크기) 직접 조작 | M5 | 같은 경로를 Core `writeConfig` 테스트와 CLI `config-set`으로는 확인 |
| Office(Word/Excel) 소스의 CF_HTML·이미지 | S-3 | Cloud PC에 Office 없음 |
| Mac RTF 전용 앱(TextEdit/Notes/Pages/Word) 실제 복사 | M3 | 합성 RTF로는 확인 |
| Workers Free 일일 한도 실측 | M2 | 실제 사용 패턴으로 |

## 4. 알려진 문제·한계와 선택지

- **Mac 서명(D-55):** Apple Development 인증서가 2026-09-30 원격 폐기되어 자체 서명 `ClipSync Dev`로 전환. 팀 ID가 없어 **Mac 앱을 다시 빌드할 때마다 Keychain 확인 창**(로그인 암호 + "항상 허용")이 한 번 뜬다. 불편하면 Xcode → Settings → Accounts에서 Apple Development 인증서를 재발급하고 `mac/scripts/build-app.sh`의 `SIGN_SHA1`을 바꾼다(CN이 같으면 기존 권한이 이어질 가능성이 큼). 폐기 원인은 확인하지 못했다.
- **3대 이상 기기:** 먼저 적용한 기기가 본문을 지워 나머지는 이미지·파일을 받지 못함 (v1 한계, spec §12).
- **pending은 메모리만(D-59):** 오프라인 중 앱을 재시작하면 보관한 항목이 사라진다.
- **로컬 우선 판정(D-58):** OS 변경 카운터 기반. 다른 클립보드 도구가 클립보드를 다시 쓰면 "로컬이 더 새것"으로 보고 재연결 catch-up 적용을 건너뛸 수 있다(실시간 이벤트는 항상 적용).
- **보낸 기기는 자기 미디어를 캐시하지 않음(D-62):** 자기가 보낸 이미지·파일은 상대가 받아 삭제하면 목록에서 비활성.
- **진행률(D-53):** "보내는 중…/받는 중…" 상태만 있고 퍼센트는 없음.
- **Mac 파일 약속(file promise):** 사진·메일 첨부처럼 파일 URL 대신 promise로 복사되는 경우는 지원하지 않음.
- **Mac pasteboard 경쟁:** 다른 앱이 `clearContents` 후 쓰기 직전에 250 ms 폴링이 끼면 그 복사를 놓칠 수 있음(드묾, 미대응). Windows는 같은 문제를 M5a에서 수정함.
- **401 rate limit은 확률적:** Rate Limiting binding이 위치별·최종 일관성이라 순차 소량 실패는 통과할 수 있음.

## 5. 개발 환경 메모

- **자동 테스트는 로컬 서버로:** `cd server && npx wrangler dev --port 8787` 후 `CLIPSYNC_URL=http://localhost:8787 CLIPSYNC_PASSPHRASE="abacus abdomen abide abnormal abrasion abroad absence"`로 TS/Swift/C# 교차 테스트. `clipsync-dev`는 실제 앱끼리의 확인에만 쓴다.
- **로컬 서버를 끌 때는 그 프로세스만:** 띄울 때 PID를 기록해 그 PID만 종료한다. `pkill -f workerd` 같은 이름 패턴 종료는 **같은 Mac의 다른 프로젝트 개발 서버까지 죽인다**(2026-09-30 실제 발생: `~/workspace/work/bmx/brewloop`의 `wrangler dev --port 8788`이 멈춤).
- **Mac 앱을 다른 서버로 잠시 돌리는 법:** 앱 종료 → `defaults write com.tobylee.clipsync serverURL <URL>` → `lastSeq lastAppliedSeq configVersion vaultConfig ownChangeCount applyWatermark pausedUntil` 삭제 → 실행 (같은 빌드면 Keychain 창 없음). 로컬 서버는 `NSAllowsLocalNetworking`으로 허용됨.
- **Cloud PC 출력 확인:** RDP 클립보드 리디렉션이 꺼져 있으므로 Mac 스크린샷(Windows App 창)으로 받는다. 파일명에 U+202F가 있어 복사 후 읽는다.
- **스파이크 브랜치** `spike/s3`, `spike/s4`는 원격에 남아 있다(버리는 코드). 필요 없으면 삭제.
