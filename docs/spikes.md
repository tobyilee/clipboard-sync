# M0 스파이크 결과

> 형식: 가정 / 결과 / 설계 변경 여부. 설계 변경이 필요하면 spec을 먼저 수정하고 결정 로그에 D-번호를 추가한다.
> 계획: [02-implementation-plan.md](./02-implementation-plan.md) §2.2

| # | 가정 | 상태 | 결과 | 설계 변경 |
|---|---|---|---|---|
| S-1 | Workers Free에서 R2 binding + 20/50MB 스트리밍 업/다운로드 (DO 메모리 미사용) | ✅ 통과 | Free 플랜에서 R2 binding 동작. `R2.put(key, request.body)` 스트리밍으로 20 MiB(PUT 4.3s/GET 1.7s), 50 MiB(10.3s/1.6s), 51 MiB(9.9s/1.6s) 모두 성공, 다운로드 바이트 일치, DELETE 후 404. 이 스파이크는 Worker→R2만 검증(DO 미경유). 실제 설계도 Worker가 스트리밍하고 DO는 메타만 다루므로 DO 메모리와 무관. 51 MiB 하드 캡 초과 거부(413)와 Free 일일 한도는 M2에서 확인 | 없음 (D-15 유지) |
| S-2 | macOS pasteboard 권한 동작, 안정 서명 ID로 재빌드 시 권한 유지 | 대기 | | |
| S-3 | Windows CF_HTML/PNG/CF_HDROP 읽기·쓰기 | 대기 | | |
| S-4 | Swift↔C# PBKDF2/HKDF/AES-GCM 상호운용 | 대기 | | |
| S-5 | 클라우드 Windows RDP 클립보드 리디렉션 off 동작 | 대기 | | |

## 환경 메모 (2026-09-29)
- 개발 Mac: macOS 26.6.2, Xcode 27.0 (SDK MacOSX27.0), Swift 6.4, Node 24, wrangler 4.143.0
- Cloudflare 계정: `toby@epril.com` (Account ID `8121708927a91d4bab87da8b49fde255`), R2 활성화 완료, workers.dev 서브도메인 `clipboardsync` 등록 (서버 URL은 `https://<worker>.clipboardsync.workers.dev`). 신규 서브도메인은 TLS 인증서가 준비되기까지 수 분 걸린다 (그동안 TLS 핸드셰이크 실패).
- `xcode-select`가 CommandLineTools를 가리킴 → Xcode.app로 전환 필요
