# Clipboard Sync

Mac ↔ Windows 양방향 클립보드 자동 동기화 (E2EE, Cloudflare Workers + Durable Object + R2).

## 문서
1. [요구사항](docs/00-requirements.md)
2. [기술 명세](docs/01-tech-spec.md)
3. [구현 계획](docs/02-implementation-plan.md)
4. [핸드오프](docs/03-handoff.md)
5. [M0 스파이크 결과](docs/spikes.md)

## 구조
- `protocol/` — 바이트 포맷·테스트 벡터·TypeScript 참조 구현(CLI 테스트 피어)
- `server/` — Cloudflare Worker + Durable Object
- `mac/` — macOS 메뉴바 앱 (Swift)
- `windows/` — Windows 트레이 앱 (.NET 10)
