import AppKit
import ClipSyncCore
import Foundation

/// 감시 → 송신, WebSocket → 수신 적용 (spec 6.2~6.5). 로컬 우선·pending·이미지/파일·수신 후 삭제는 M5/M6.
@MainActor
final class SyncEngine {
    enum Connection: Equatable { case off, connecting, connected, offline }

    struct RecentItem: Identifiable {
        var seq: Int
        var id: String
        var deviceId: String
        var kinds: [String]
        var preview: String
        var createdAt: UInt64
        var purged: Bool
    }

    private(set) var connection: Connection = .off
    private(set) var recent: [RecentItem] = []
    private(set) var lastMessage: String?
    var onChange: () -> Void = {}

    private let settings = Settings.shared
    private var client: ServerClient?
    private var runTask: Task<Void, Never>?
    private var applyTask: Task<Void, Never>?
    private var sendChain: Task<Void, Never>?
    private var timer: Timer?
    private var lastChangeCount = NSPasteboard.general.changeCount
    private var ring = RecentHashRing()
    private let allowedKinds: Set<String> = ["text", "html"]   // config 없음 = 텍스트만 (fail-closed, 4.4)

    var isPaused: Bool { settings.pausedUntil.map { $0 > Date() } ?? false }
    private var canApply: Bool { settings.syncEnabled && settings.receiveEnabled && !isPaused }
    private var canSend: Bool { settings.syncEnabled && settings.sendEnabled && !isPaused }

    // MARK: 시작/중지

    func start() {
        stop()
        guard settings.syncEnabled, let url = settings.serverURL,
              let pass = try? KeychainStore.load(), let keys = try? deriveKeys(passphrase: pass),
              let c = try? ServerClient(baseURL: url, keys: keys, deviceId: settings.deviceId) else { return }
        client = c
        lastChangeCount = NSPasteboard.general.changeCount   // 시작 시점의 클립보드는 보내지 않는다
        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
        timer?.tolerance = 0.1
        runTask = Task { [weak self] in await self?.runLoop(c) }
    }

    func stop() {
        timer?.invalidate(); timer = nil
        runTask?.cancel(); runTask = nil
        applyTask?.cancel(); applyTask = nil
        client = nil
        connection = .off
        onChange()
    }

    func restart() { start() }

    // MARK: 연결 / 수신

    private func runLoop(_ c: ServerClient) async {
        var backoff = 1.0
        while !Task.isCancelled {
            connection = .connecting; onChange()
            for await ev in c.events() {
                if Task.isCancelled { return }
                switch ev {
                case .hello(let seq, _):
                    connection = .connected; lastMessage = nil; backoff = 1; onChange()
                    await catchUp(c, helloSeq: seq)
                case .item(let item, let inline):
                    await handle([item], inline: [item.id: inline], client: c)
                case .bodyPurged(let id):
                    if let i = recent.firstIndex(where: { $0.id == id }) { recent[i].purged = true; onChange() }
                case .config:
                    break   // vault 설정은 M5
                case .closed(let why):
                    lastMessage = why
                }
            }
            if Task.isCancelled { return }
            connection = .offline; onChange()
            try? await Task.sleep(for: .seconds(backoff * Double.random(in: 0.8...1.2)))
            backoff = min(60, backoff * 2)
        }
    }

    /// 재접속/첫 접속 직후: 첫 실행이면 last_seq=hello.seq (과거 항목 자동 적용 방지), 아니면 since=last_seq로 따라잡는다.
    /// 이 함수가 도는 동안 도착한 WS 이벤트는 스트림에 쌓였다가 이후 순서대로 처리된다 (D-42).
    private func catchUp(_ c: ServerClient, helloSeq: Int) async {
        guard let last = settings.lastSeq else {
            settings.lastSeq = helloSeq
            if let items = try? await c.list(since: 0) { await handle(items, inline: [:], client: c, apply: false) }
            return
        }
        do {
            let items = try await c.list(since: last)
            await handle(items, inline: [:], client: c)
        } catch {
            lastMessage = "목록 조회 실패: \(error)"; onChange()
        }
    }

    private func handle(_ items: [ServerItem], inline: [String: [UInt8]?], client c: ServerClient, apply: Bool = true) async {
        guard let last = settings.lastSeq else { return }
        var headers: [String: ReceivedItem] = [:]
        var candidates: [CatchUpCandidate] = []
        for it in items {
            guard let r = try? await c.receive(it, withBody: false) else { continue }   // 복호화 실패는 폐기 (6.3-5)
            headers[it.id] = r
            let kinds = (r.header["kinds"] as? [String]) ?? []
            candidates.append(CatchUpCandidate(seq: it.seq, deviceId: it.deviceId, purged: it.purged, kinds: kinds))
            addRecent(it, header: r)
        }
        let plan = planCatchUp(items: candidates, lastSeq: last, selfDevice: settings.deviceId,
                               canApply: apply && canApply, allowedKinds: allowedKinds)
        settings.lastSeq = plan.newLastSeq
        onChange()
        if let t = plan.target, let it = items.first(where: { $0.seq == t.seq }) {
            applyTask?.cancel()   // 더 새 항목이 오면 진행 중인 이전 다운로드는 취소 (6.3-2)
            applyTask = Task { [weak self] in await self?.apply(it, inline: inline[it.id] ?? nil, client: c) }
        }
    }

    private func addRecent(_ it: ServerItem, header r: ReceivedItem) {
        guard !recent.contains(where: { $0.id == it.id }) else { return }
        recent.append(RecentItem(seq: it.seq, id: it.id, deviceId: it.deviceId, kinds: (r.header["kinds"] as? [String]) ?? [],
                                 preview: (r.header["preview"] as? String) ?? "", createdAt: it.createdAt, purged: it.purged))
        recent.sort { $0.seq > $1.seq }
        if recent.count > 20 { recent.removeLast(recent.count - 20) }
    }

    /// 원격 항목을 클립보드에 적용한다. 텍스트 항목은 서버 본문을 지우지 않는다 (6.3-3).
    func apply(_ it: ServerItem, inline: [UInt8]?, client c: ServerClient) async {
        do {
            let r = try await c.receive(it, inlineBody: inline, withBody: true)
            if Task.isCancelled { return }
            guard let entries = r.entries else { lastMessage = "서버에서 삭제된 항목입니다"; onChange(); return }
            let plain = entries.first { $0.type == 1 }.map { String(decoding: $0.data, as: UTF8.self) }
            let html = entries.first { $0.type == 2 }.map { String(decoding: $0.data, as: UTF8.self) }
            let hash = contentHash(plainText: plain, html: html)
            if let hash { ring.add(hash) }   // 되돌아오는 변경을 업로드하지 않도록 (D-24)
            if let hash, hash == PasteboardIO.currentHash() {
                // D-49: 이미 같은 내용이면 다시 쓰지 않는다 (RDP 리디렉션 on에서 재쓰기→재전파를 줄인다)
            } else {
                lastChangeCount = PasteboardIO.write(entries: entries, itemId: it.id)
            }
            settings.lastAppliedSeq = max(settings.lastAppliedSeq ?? 0, it.seq)
        } catch {
            if !Task.isCancelled { lastMessage = "적용 실패: \(error)"; onChange() }
        }
    }

    /// 최근 항목 메뉴에서 선택: 서버에 본문이 남아 있으면 받아서 복원한다.
    func restore(_ item: RecentItem) {
        guard let c = client else { return }
        Task { [weak self] in
            guard let self else { return }
            guard let list = try? await c.list(since: max(0, item.seq - 1)), let it = list.first(where: { $0.id == item.id }) else {
                self.lastMessage = "서버에서 삭제됨"; self.onChange(); return
            }
            await self.apply(it, inline: nil, client: c)
        }
    }

    // MARK: 감시 / 송신

    private func poll() {
        let pb = NSPasteboard.general
        guard pb.changeCount != lastChangeCount else { return }
        lastChangeCount = pb.changeCount
        guard canSend, let c = client else { return }   // 꺼져 있으면 보내지 않고 pending도 만들지 않는다 (6.2-3)

        guard case .content(let content) = PasteboardIO.read(pb) else { return }
        guard let hash = contentHash(plainText: content.plain, html: content.html) else { return }
        if ring.contains(hash) { return }
        ring.add(hash)

        var entries: [BundleEntry] = []
        var kinds: [String] = []
        if let p = content.plain { entries.append(BundleEntry(type: 1, name: "", data: [UInt8](p.utf8))); kinds.append("text") }
        if let h = content.html { entries.append(BundleEntry(type: 2, name: "", data: [UInt8](h.utf8))); kinds.append("html") }
        let plainSize = entries.reduce(0) { $0 + $1.data.count }
        guard plainSize <= 1 << 20 else { lastMessage = "1 MiB를 넘는 텍스트는 보내지 않습니다"; onChange(); return }

        let prepared: ServerClient.PreparedItem
        do { prepared = try c.prepare(entries: entries, kinds: kinds, previewText: content.plain) } catch { return }
        let prev = sendChain
        sendChain = Task { [weak self] in
            await prev?.value
            await self?.upload(prepared, kinds: kinds, preview: previewOf(content.plain ?? ""), client: c)
        }
    }

    /// 지수 백오프로 최대 3회. 같은 PreparedItem을 재전송하므로 서버에 중복 항목이 생기지 않는다 (idempotent).
    private func upload(_ item: ServerClient.PreparedItem, kinds: [String], preview: String, client c: ServerClient) async {
        var delay = 1.0
        for attempt in 1...3 {
            do {
                let seq = try await c.upload(item)
                // 서버는 업로더의 소켓에 이벤트를 보내지 않으므로 내가 보낸 항목을 최근 항목에 직접 추가한다.
                recent.append(RecentItem(seq: seq, id: item.id, deviceId: settings.deviceId, kinds: kinds, preview: preview,
                                         createdAt: item.createdAt, purged: false))
                recent.sort { $0.seq > $1.seq }
                if recent.count > 20 { recent.removeLast(recent.count - 20) }
                lastMessage = nil; onChange(); return
            } catch {
                if attempt == 3 { lastMessage = "전송 실패: \(error)"; onChange(); return }
                try? await Task.sleep(for: .seconds(delay)); delay *= 2
            }
        }
    }
}
