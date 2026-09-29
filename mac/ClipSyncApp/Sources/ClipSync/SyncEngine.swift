import AppKit
import ClipSyncCore
import Foundation

/// 감시 → 송신, WebSocket → 수신 적용 (spec 6.2~6.5), vault 설정(4.4, D-50). 로컬 우선·pending·수신 후 삭제는 M6, 파일은 M5b.
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
    /// "보내는 중…"/"받는 중…" (D-53)
    private(set) var busy: String?
    var onChange: () -> Void = {}

    private let settings = Settings.shared
    private var client: ServerClient?
    private var runTask: Task<Void, Never>?
    private var applyTask: Task<Void, Never>?
    private var sendChain: Task<Void, Never>?
    private var timer: Timer?
    private var lastChangeCount = NSPasteboard.general.changeCount
    private var ring = RecentHashRing()

    var config: VaultConfig { settings.vaultConfig }
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
        Log.info("engine start device=\(settings.deviceId) server=\(url.host ?? "") lastSeq=\(settings.lastSeq.map(String.init) ?? "nil") config.v=\(settings.configVersion)")
    }

    func stop() {
        if client != nil { Log.info("engine stop") }
        timer?.invalidate(); timer = nil
        runTask?.cancel(); runTask = nil
        applyTask?.cancel(); applyTask = nil
        client = nil
        connection = .off
        onChange()
    }

    func restart() { start() }

    private func setMessage(_ m: String?, notify: Bool = false) {
        lastMessage = m
        if notify, let m { Notifier.shared.post(m) }
        onChange()
    }

    // MARK: vault 설정 (D-50)

    /// 서버가 준 설정을 수용한다: 복호화 실패·version 이하이면 무시하고 캐시 유지.
    private func accept(_ blob: ConfigBlob, from c: ServerClient) {
        guard blob.version > settings.configVersion else { return }
        guard let cfg = try? openConfig(blob, key: c.keys.encKey) else {
            Log.error("config v\(blob.version) decrypt failed — ignored"); return
        }
        store(version: blob.version, config: cfg)
    }

    private func store(version: Int, config cfg: VaultConfig) {
        settings.configVersion = version
        settings.vaultConfig = cfg
        Log.info("config v\(version) images=\(cfg.images) files=\(cfg.files) max=\(cfg.maxMediaBytes >> 20)MiB")
        onChange()
    }

    /// Mac 메뉴에서 설정 변경 (Mac만 편집, D-17). 412면 최신을 다시 읽어 바꾼 필드만 재적용 (ServerClient.writeConfig).
    func updateConfig(_ change: @escaping @Sendable (inout VaultConfig) -> Void) {
        guard let c = client else { setMessage("연결되지 않아 설정을 바꿀 수 없습니다"); return }
        Task { [weak self] in
            do {
                let (v, cfg) = try await c.writeConfig(change)
                self?.store(version: v, config: cfg)   // 자기가 방금 PUT한 version은 항상 수용 (D-50)
            } catch {
                Log.error("config write: \(error)")
                self?.setMessage("설정 저장 실패: \(error)", notify: true)
            }
        }
    }

    // MARK: 연결 / 수신

    private func runLoop(_ c: ServerClient) async {
        var backoff = 1.0
        while !Task.isCancelled {
            connection = .connecting; onChange()
            for await ev in c.events() {
                if Task.isCancelled { return }
                switch ev {
                case .hello(let seq, let cfg):
                    connection = .connected; lastMessage = nil; backoff = 1; onChange()
                    Log.info("connected hello.seq=\(seq)")
                    if let cfg { accept(cfg, from: c) }
                    await catchUp(c, helloSeq: seq)
                case .item(let item, let inline):
                    Log.info("event item seq=\(item.seq) id=\(item.id) from=\(item.deviceId.prefix(8)) inline=\(inline != nil) bytes=\(item.bodySize)")
                    await handle([item], inline: [item.id: inline], client: c)
                case .bodyPurged(let id):
                    if let i = recent.firstIndex(where: { $0.id == id }) { recent[i].purged = true; onChange() }
                case .config(let blob):
                    accept(blob, from: c)
                case .closed(let why):
                    lastMessage = why
                    Log.info("disconnected: \(why ?? "-")")
                }
            }
            if Task.isCancelled { return }
            connection = .offline; onChange()
            Log.info("reconnect in ~\(Int(backoff))s")
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
            setMessage("목록 조회 실패: \(error)")
            Log.error("catch-up list: \(error)")
        }
    }

    private func handle(_ items: [ServerItem], inline: [String: [UInt8]?], client c: ServerClient, apply: Bool = true) async {
        guard let last = settings.lastSeq else { return }
        var candidates: [CatchUpCandidate] = []
        for it in items {
            guard let r = try? await c.receive(it, withBody: false) else { continue }   // 복호화 실패는 폐기 (6.3-5)
            let kinds = (r.header["kinds"] as? [String]) ?? []
            candidates.append(CatchUpCandidate(seq: it.seq, deviceId: it.deviceId, purged: it.purged, kinds: kinds))
            addRecent(it, header: r)
        }
        let plan = planCatchUp(items: candidates, lastSeq: last, selfDevice: settings.deviceId,
                               canApply: apply && canApply, allowedKinds: config.allowedKinds)
        settings.lastSeq = plan.newLastSeq
        onChange()
        Log.info("handled \(items.count) item(s) lastSeq=\(plan.newLastSeq) target=\(plan.target.map { String($0.seq) } ?? "-")")
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

    /// 원격 항목을 클립보드에 적용한다. 수신 후 삭제(이미지/파일)는 M6.
    func apply(_ it: ServerItem, inline: [UInt8]?, client c: ServerClient) async {
        let large = inline == nil && it.bodySize > 1 << 20
        if large { busy = "받는 중…"; onChange() }
        defer { if large { busy = nil; onChange() } }
        do {
            let r = try await c.receive(it, inlineBody: inline, withBody: true)
            if Task.isCancelled { return }
            guard let entries = r.entries else { setMessage("서버에서 삭제된 항목입니다"); return }
            let plain = entries.first { $0.type == 1 }.map { String(decoding: $0.data, as: UTF8.self) }
            let html = entries.first { $0.type == 2 }.map { String(decoding: $0.data, as: UTF8.self) }
            if let png = entries.first(where: { $0.type == 3 }).map({ Data($0.data) }) {
                // 픽셀 해시(D-54)와 TIFF 변환은 메인 스레드 밖에서
                let (hash, tiff) = await Task.detached { (imagePixelHash(png), tiffData(fromPNG: png)) }.value
                if Task.isCancelled { return }
                if let hash { ring.add(hash) }
                lastChangeCount = PasteboardIO.write(entries: entries, tiff: tiff, itemId: it.id)
                Log.info("applied image seq=\(it.seq) id=\(it.id) bytes=\(png.count)")
            } else {
                let hash = contentHash(plainText: plain, html: html)
                if let hash { ring.add(hash) }   // 되돌아오는 변경을 업로드하지 않도록 (D-24)
                if let hash, hash == PasteboardIO.currentTextHash() {
                    Log.info("apply skipped (same content) seq=\(it.seq) id=\(it.id)")   // D-49 (텍스트만)
                } else {
                    lastChangeCount = PasteboardIO.write(entries: entries, itemId: it.id)
                    Log.info("applied seq=\(it.seq) id=\(it.id)")
                }
            }
            settings.lastAppliedSeq = max(settings.lastAppliedSeq ?? 0, it.seq)
        } catch {
            if !Task.isCancelled { setMessage("적용 실패: \(error)"); Log.error("apply seq=\(it.seq): \(error)") }
        }
    }

    /// 최근 항목 메뉴에서 선택: 서버에 본문이 남아 있으면 받아서 복원한다.
    func restore(_ item: RecentItem) {
        guard let c = client else { return }
        Task { [weak self] in
            guard let self else { return }
            guard let list = try? await c.list(since: max(0, item.seq - 1)), let it = list.first(where: { $0.id == item.id }) else {
                self.setMessage("서버에서 삭제됨"); return
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

        let shape = PasteboardIO.shape(pb)
        if shape.own { return }
        switch classify(hasFiles: shape.hasFiles, hasImage: shape.hasImage, hasText: shape.hasText, config: config) {
        case .ignore:
            return   // 타입 off 등은 조용히 무시 (4.5)
        case .files:
            Log.info("files copy ignored (M5b)")
            return
        case .text:
            guard let content = PasteboardIO.readText(pb) else { return }
            sendText(content, client: c)
        case .image(let withText):
            guard let img = PasteboardIO.readImage(pb) else { return }
            sendImage(img, text: withText ? PasteboardIO.readText(pb) : nil, client: c)
        }
    }

    private func sendText(_ content: PasteboardIO.Content, client c: ServerClient) {
        guard let hash = contentHash(plainText: content.plain, html: content.html), !ring.contains(hash) else { return }
        ring.add(hash)
        let (entries, kinds) = textEntries(content)
        guard entries.reduce(0, { $0 + $1.data.count }) <= VaultConfig.textLimit else {
            setMessage("1 MiB를 넘는 텍스트는 보내지 않습니다", notify: true); return
        }
        guard let prepared = try? c.prepare(entries: entries, kinds: kinds, previewText: content.plain) else { return }
        enqueue(prepared, kinds: kinds, preview: previewOf(content.plain ?? ""), client: c)
    }

    private func textEntries(_ content: PasteboardIO.Content) -> ([BundleEntry], [String]) {
        var entries: [BundleEntry] = []
        var kinds: [String] = []
        if let p = content.plain { entries.append(BundleEntry(type: 1, name: "", data: [UInt8](p.utf8))); kinds.append("text") }
        if let h = content.html { entries.append(BundleEntry(type: 2, name: "", data: [UInt8](h.utf8))); kinds.append("html") }
        return (entries, kinds)
    }

    /// 이미지: TIFF→PNG 변환·픽셀 해시·봉인은 메인 스레드 밖에서 한다 (큰 이미지로 메뉴가 멈추지 않도록).
    private func sendImage(_ img: (data: Data, isPNG: Bool), text: PasteboardIO.Content?, client c: ServerClient) {
        let limit = config.maxMediaBytes
        Task { [weak self] in
            let prep = await Task.detached { () -> (png: Data, hash: String, size: (w: Int, h: Int))? in
                guard let png = img.isPNG ? img.data : pngData(fromImageData: img.data),
                      let size = pngSize([UInt8](png)), let hash = imagePixelHash(png) else { return nil }
                return (png, hash, size)
            }.value
            guard let self else { return }
            guard let prep else { Log.error("image conversion failed"); return }
            if self.ring.contains(prep.hash) { return }
            self.ring.add(prep.hash)
            var (entries, kinds) = text.map(self.textEntries) ?? ([], [])
            entries.insert(BundleEntry(type: 3, name: "", data: [UInt8](prep.png)), at: 0)
            kinds.insert("image", at: 0)
            let total = entries.reduce(0) { $0 + $1.data.count }
            guard total <= limit else {
                self.setMessage("이미지가 \(limit >> 20) MB를 넘어 보내지 않았습니다 (\(String(format: "%.1f", Double(total) / 1048576)) MB)", notify: true)
                Log.info("image too large bytes=\(total) limit=\(limit)")
                return
            }
            let prepared = await Task.detached { try? c.prepare(entries: entries, kinds: kinds, previewText: text?.plain, imageSize: prep.size) }.value
            guard let prepared else { return }
            self.enqueue(prepared, kinds: kinds, preview: text?.plain.map(previewOf) ?? "이미지 \(prep.size.w)×\(prep.size.h)", client: c)
        }
    }

    private func enqueue(_ prepared: ServerClient.PreparedItem, kinds: [String], preview: String, client c: ServerClient) {
        let prev = sendChain
        sendChain = Task { [weak self] in
            await prev?.value
            await self?.upload(prepared, kinds: kinds, preview: preview, client: c)
        }
    }

    /// 지수 백오프로 최대 3회. 같은 PreparedItem을 재전송하므로 서버에 중복 항목이 생기지 않는다 (idempotent).
    private func upload(_ item: ServerClient.PreparedItem, kinds: [String], preview: String, client c: ServerClient) async {
        let large = item.bodySize > 1 << 20
        if large { busy = "보내는 중…"; onChange() }
        defer { if large { busy = nil; onChange() } }
        var delay = 1.0
        for attempt in 1...3 {
            do {
                let seq = try await c.upload(item)
                Log.info("sent seq=\(seq) id=\(item.id) kinds=\(kinds.joined(separator: ",")) bytes=\(item.bodySize)")
                // 서버는 업로더의 소켓에 이벤트를 보내지 않으므로 내가 보낸 항목을 최근 항목에 직접 추가한다.
                recent.append(RecentItem(seq: seq, id: item.id, deviceId: settings.deviceId, kinds: kinds, preview: preview,
                                         createdAt: item.createdAt, purged: false))
                recent.sort { $0.seq > $1.seq }
                if recent.count > 20 { recent.removeLast(recent.count - 20) }
                setMessage(nil); return
            } catch {
                Log.error("upload id=\(item.id) attempt=\(attempt): \(error)")
                if attempt == 3 { setMessage("전송 실패: \(error)", notify: true); return }
                try? await Task.sleep(for: .seconds(delay)); delay *= 2
            }
        }
    }
}
