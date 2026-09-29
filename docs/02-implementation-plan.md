# Clipboard Sync — 구현 계획서

> 상태: 계획 확정 (구현 미착수)
> 작성일: 2026-09-29
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

목적: 설계의 **불확실한 가정 4가지**를 코드 몇십 줄 수준으로 먼저 검증해, 큰 구현 뒤에 설계가 무너지는 일을 막는다.

### 2.1 환경 준비
| 항목 | 내용 |
|---|---|
| Cloudflare | 계정, `wrangler` 로그인, **R2 활성화(결제 수단 필요 여부 확인)**, Workers Rate Limiting binding 사용 가능 여부 |
| Mac | Xcode(현재 macOS SDK 포함), macOS 버전 확인 (pasteboard 프라이버시 동작 대상) |
| Windows | .NET 8 SDK, Visual Studio 또는 `dotnet` CLI. Windows 기기에서 빌드·실행 가능해야 함 |
| 저장소 | `git init`, 모노레포 디렉터리(`protocol/ server/ mac/ windows/`), `.gitignore`, 루트 README (문서 링크) |

### 2.2 스파이크 (버리는 코드; 결과는 문서에 기록)
| # | 검증할 가정 | 방법 | 통과 기준 | 실패 시 |
|---|---|---|---|---|
| S-1 | Worker가 **20MB 본문을 R2로 스트리밍**할 수 있고 DO 메모리를 쓰지 않는다 | Worker에서 `PUT` 본문을 `R2.put(key, request.body)`로 저장 후 GET | 20MB, 50MB 업/다운로드 성공, 오류 없음 | 클라이언트가 R2 presigned URL로 직접 업로드하는 구조로 변경 (spec D-15 재검토) |
| S-2 | **macOS pasteboard 권한 동작**: `changeCount` 폴링은 프롬프트 없음, 내용 읽기는 프롬프트/`accessBehavior` | 최소 메뉴바 앱에서 `changeCount` 폴링 + 읽기 + `accessBehavior`/`detect*` 호출 | 실제 SDK 시그니처 확인, 허용 안내 경로 확정 | spec 7장 온보딩 설계를 실측에 맞게 수정 |
| S-3 | Windows `AddClipboardFormatListener` + **CF_HTML/PNG/CF_HDROP** 읽기·쓰기 | 작은 WinForms 앱 | 텍스트/HTML/PNG/파일이 Explorer, 브라우저, Office와 상호 붙여넣기 됨 | 표현 매핑표(spec 8장) 수정 |
| S-4 | Mac↔Win **PBKDF2/HKDF/AES-GCM 상호운용** | Swift와 C#에서 같은 입력으로 파생·암호화 | 같은 키/복호화 성공 | M1로 그대로 진행 (실패는 사실상 구현 버그) |

- 스파이크 결과는 `docs/spikes.md`에 한 줄씩 기록한다 (가정 / 결과 / 설계 변경 여부).
- S-1, S-2가 가장 위험하다. **S-1 실패 시 서버 구조가, S-2 실패 시 Mac 감시 방식이 바뀐다.**

**완료 기준:** 4개 스파이크의 결과가 기록되고, 설계 변경이 필요하면 01-tech-spec에 먼저 반영되었다.

## 3. M1 — protocol (테스트 벡터)

산출물
- `protocol/PROTOCOL.md`: 키 파생, 암호화 단위, AAD 레이아웃, 번들 바이트 포맷, config 포맷을 spec에서 뽑아 **구현의 단일 출처**로 정리
- `protocol/test-vectors.json`: passphrase → master → enc_key/auth_token/vault_id, 고정 nonce 기반 header/body/config 암호문, 번들 인코딩 바이트, 잘못된 AAD 복호화 실패 케이스
- 벡터 생성 스크립트(한 번 실행해 결과를 커밋; 참조 구현은 TypeScript 또는 Python)

**완료 기준:** 참조 구현이 벡터를 만들고, Swift 패키지와 C# 라이브러리에서 **같은 벡터를 읽는 단위 테스트가 통과**한다 (Mac/Windows 상호운용을 서버·UI 이전에 확정).

## 4. M2 — 서버 (Worker + Durable Object + R2)

세부 작업 순서
1. 프로젝트 골격, `wrangler.toml`(DO, R2, secret `VAULT_ID`, rate limit binding, R2 lifecycle 1일)
2. 인증 미들웨어 (상수시간 비교, 401 rate limit, 51 MiB `Content-Length` 검사)
3. DO 스키마와 `PUT /body`(DO/R2 분기) → `POST /items` 커밋(seq, 20개 cap, broadcast)
4. `GET /items`, `GET /items/{id}/body`
5. WebSocket hibernation (`hello`, `item`, auto-response ping/pong)
6. alarm: 24h 만료, 고아 본문 정리 (10분)
7. 이 단계에서는 `DELETE /body`, `config` API도 함께 구현한다 (서버는 한 번에 끝내는 편이 단순).

**완료 기준**
- spec 10장의 서버 테스트 목록이 `vitest`로 모두 통과
- `wrangler dev`와 실제 배포 환경에서 `curl`/스크립트로 20MB 업로드→다운로드→삭제(410) 확인
- 두 개의 WebSocket 클라이언트 스크립트로 `item`/`body_purged`/`config` 이벤트 수신 확인

## 5. M3 — macOS 앱 (텍스트 우선)

세부 작업 순서
1. 메뉴바 앱 골격 (`LSUIElement`, `NSStatusItem`, SwiftUI 메뉴), 상태 모델
2. 온보딩: passphrase 생성/입력, 서버 URL, 키 파생, Keychain 저장, `vault_id` 표시
3. `changeCount` 감시 + 텍스트/HTML 읽기 + 에코 마커
4. 송신 (암호화, PUT/POST), WebSocket 수신, 클립보드 적용
5. 메뉴: 전체 on/off, 일시정지, 보내기/받기, 최근 항목 20개(선택 시 복원)
6. 자동 시작 (`SMAppService`)

**완료 기준:** Mac 앱 두 인스턴스(또는 앱 + 서버 테스트 클라이언트) 사이에서 텍스트/HTML이 양방향 동기화되고, 에코 루프가 없으며, 전체 off·일시정지·방향 토글이 동작한다.

## 6. M4 — Windows 앱 (텍스트 우선)

세부 작업 순서
1. WinForms 트레이 앱 골격, `NotifyIcon` 메뉴
2. 온보딩(passphrase/서버 URL 입력), 키 파생, Credential Manager 저장
3. 클립보드 리스너 (STA, 재시도/백오프, debounce), 텍스트/HTML(`CF_HTML` 생성·파싱)
4. 송수신, 에코 마커(`RegisterClipboardFormat`), `CanUploadToCloudClipboard=0`
5. 메뉴: Mac과 동일한 토글·최근 항목, 자동 시작(`HKCU Run`)

**완료 기준:** **Mac ↔ Windows 실제 기기 간 텍스트/HTML 양방향 동기화**가 동작한다. 이것이 프로젝트의 첫 번째 사용자 가치 검증 지점이다 (M3+M4 통합 데모).

## 7. M5 — vault 설정 + 이미지/파일

세부 작업 순서
1. config 모델/암호화/`PUT`(If-Match)·`GET`, 클라이언트 fail-closed 기본값 (Mac, Windows 공통)
2. Mac 메뉴: "동기화 대상"(이미지/파일), "미디어 최대 크기" → config PUT. Windows: 읽기 전용 표시
3. 송신 판별(spec 4.5), 크기 검사, 이미지(PNG)와 파일 번들 인코딩, 대용량 스트리밍 업로드와 진행률
4. 수신 적용 (Mac: `NSPasteboardItem` + file URL, Windows: PNG+DIBV5, `CF_HDROP`), 파일명 sanitize
5. 로컬 캐시 폴더 구조 (정리 정책은 M6)

**완료 기준**
- 기본 상태(OFF)에서 이미지/파일 복사는 무시되고 텍스트만 동기화됨
- Mac에서 이미지·파일을 켜면 Windows에도 반영되어 양쪽에서 동작
- 20MB 초과 시 알림 후 미전송, 5/10/50MB 변경이 즉시 반영
- 파일이 꺼진 상태에서 Finder 파일 복사가 **파일명 텍스트로 새지 않음**

## 8. M6 — 수신 후 삭제 + 오프라인/재연결

세부 작업 순서
1. 적용 성공 후 `DELETE /body` 호출 (이미지/파일 항목만), `body_purged` 이벤트 처리, 목록의 "서버에서 삭제됨" 표시
2. 로컬 캐시 (24h / 200 MiB LRU), 캐시에서의 복원 경로
3. catch-up (`since=last_seq`), "가장 최신 하나만 적용", 새 항목 도착 시 진행 중 다운로드 취소
4. 재연결(백오프+jitter), 슬립/웨이크 즉시 재연결, 최초 실행 시 과거 항목 자동 적용 금지

**완료 기준:** 서버 저장소에서 적용 완료한 이미지/파일 본문이 사라지고(R2 객체 삭제 확인), 오프라인이던 기기가 재접속하면 최신 항목만 클립보드에 적용되며, 삭제된 항목은 캐시가 있는 기기에서만 복원된다.

## 9. M7 — 마감

- macOS pasteboard 권한 온보딩과 "권한 필요" 상태 (S-2 결과 반영), TCC 안내
- 알림 (크기 초과, 연결 오류, 권한 필요), Windows toast AUMID
- passphrase 변경/키 교체 절차 문서화 (새 `VAULT_ID` 등록, 서버 데이터 폐기)
- 설치 문서 `docs/03-setup.md`: 서버 배포(`wrangler deploy`, R2, secret) → Mac 설치 → Windows 설치
- spec 10장의 **수동 E2E 매트릭스 전체 수행**, 결과를 `docs/e2e-results.md`에 기록

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

## 11. 작업 방식

- **브랜치/커밋**: 마일스톤 단위로 작업하고 완료 기준 통과 시점에 커밋 (커밋 메시지에 `M<번호>` 접두).
- **문서 동기화**: 구현 중 spec을 바꿔야 하면 코드보다 **spec을 먼저 수정**하고 결정 로그(D-번호)에 추가한다.
- **범위 통제**: v1 범위 밖 항목(비밀번호 관리자 제외, 압축, 3대 이상 기기 등)은 이슈로만 기록하고 구현하지 않는다.
- **서버 리소스**: 개발 중에는 별도 Workers 이름(`clipsync-dev`)과 별도 R2 버킷을 사용하고, 운영 배포는 M7에서 한다.

## 12. 시작 전에 정해야 할 것 (구현 착수 시 확인)

1. Cloudflare 계정에서 R2 활성화 여부 (결제 수단 등록 가능?)
2. Windows 개발/테스트를 어느 기기에서 할지 (실제 사용 중인 Windows PC 가정)
3. Git 원격 저장소(GitHub 등) 사용 여부
