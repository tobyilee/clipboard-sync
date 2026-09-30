import AppKit
import ClipSyncCore
import Foundation

/// 감시 → 송신, WebSocket → 수신 적용 (spec 6.2~6.5), vault 설정(4.4, D-50),
/// 수신 후 삭제·캐시(D-61, D-62), 로컬 우선·pending·재개 catch-up(D-58~D-60).
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

    /// 송신 큐의 한 단위. `observed`는 이 내용을 읽었을 때의 changeCount (D-58).
    private struct Outgoing {
        var item: ServerClient.PreparedItem
        var kinds: [String]
        var preview: String
        var observed: Int
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
    private var cleanupTimer: Timer?
    private var lastChangeCount = NSPasteboard.general.changeCount
    private var ring = RecentHashRing()
    /// D-59: 전송 계층 실패로 못 보낸 가장 최근 항목 하나 (메모리만)
    private var pending: Outgoing?
    private var outageAlerted = false
    private var wasApplyEnabled = false

    var config: VaultConfig { settings.vaultConfig }
    var isPaused: Bool { settings.pausedUntil.map { $0 > Date() } ?? false }
    var hasPending: Bool { pending != nil }
    private var canApply: Bool { settings.syncEnabled && settings.receiveEnabled && !isPaused }
    private var canSend: Bool { settings.syncEnabled && settings.sendEnabled && !isPaused }

    nonisolated static let cacheRoot = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("com.tobylee.clipsync/incoming", isDirectory: true)
    nonisolated static func cacheDir(_ id: String) -> URL { cacheRoot.appendingPathComponent(id, isDirectory: true) }
    func hasCache(_ id: String) -> Bool { isCacheId(id) && FileManager.default.fileExists(atPath: Self.cacheDir(id).path) }

    // MARK: 시작/중지

    func start() {
        stop()
        guard settings.syncEnabled, let url = settings.serverURL,
              let pass = try? KeychainStore.load(), let keys = try? deriveKeys(passphrase: pass),
              let c = try? ServerClient(baseURL: url, keys: keys, deviceId: settings.deviceId) else { return }
        client = c
        lastChangeCount = NSPasteboard.general.changeCount   // 시작 시점의 클립보드는 보내지 않는다
        wasApplyEnabled = canApply
        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
        timer?.tolerance = 0.1
        cleanupTimer = Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.cleanupCache() }
        }
        cleanupCache()
        runTask = Task { [weak self] in await self?.runLoop(c) }
        Log.info("engine start device=\(settings.deviceId) server=\(url.host ?? "") lastSeq=\(settings.lastSeq.map(String.init) ?? "nil") config.v=\(settings.configVersion)")
    }

    func stop() {
        if client != nil { Log.info("engine stop") }
        timer?.invalidate(); timer = nil
        cleanupTimer?.invalidate(); cleanupTimer = nil
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

    // MARK: 로컬 우선 (D-58)

    /// 우리가 쓰거나 보낸 내용이 지금 클립보드에 있다고 기록한다.
    private func markOwn(_ changeCount: Int) {
        // 늦게 끝난 옛 업로드가 더 새 기록을 덮지 않게 한다. 저장값이 지금 카운터보다 크면 재부팅 등으로 초기화된 것이므로 받아들인다.
        if let own = settings.ownChangeCount, own <= NSPasteboard.general.changeCount, changeCount < own { return }
        settings.ownChangeCount = changeCount
    }

    /// 마지막으로 쓰거나 보낸 뒤 사용자가 새로 복사했는가. 비어 있으면 아니다(일반 규칙으로 적용).
    private func localIsNewer() -> Bool {
        let pb = NSPasteboard.general
        let types = pb.types ?? []
        if types.isEmpty || types.contains(PasteboardIO.markerType) { return false }
        guard let own = settings.ownChangeCount else { return true }
        return pb.changeCount != own
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
                    connection = .connected; lastMessage = nil; backoff = 1; outageAlerted = false; onChange()
                    Log.info("connected hello.seq=\(seq)")
                    if let cfg { accept(cfg, from: c) }
                    await catchUpOnConnect(c, helloSeq: seq)
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

    /// 재접속/첫 접속 직후 (D-42, D-59, D-60): 첫 실행이면 last_seq=hello.seq (과거 항목 자동 적용 방지).
    /// 아니면 pending을 먼저 올리고, 적용 워터마크(없으면 last_seq)부터 따라잡는다.
    /// 이 함수가 도는 동안 도착한 WS 이벤트는 스트림에 쌓였다가 이후 순서대로 처리된다.
    private func catchUpOnConnect(_ c: ServerClient, helloSeq: Int) async {
        guard let last = settings.lastSeq else {
            settings.lastSeq = helloSeq
            settings.applyWatermark = nil
            if let items = try? await c.list(since: 0) { await handle(items, inline: [:], client: c, apply: false) }
            return
        }
        await catchUp(c, since: min(last, settings.applyWatermark ?? last))
    }

    private func catchUp(_ c: ServerClient, since: Int) async {
        let hadPending = await flushPending()
        do {
            // 서버는 최대 20개만 보관하므로 전부 받아 최근 항목을 채우고(재시작 뒤에도 목록 유지), 적용 판단은 since 이후만 본다.
            let items = try await c.list(since: 0)
            let hold = hadPending || localIsNewer()
            await handle(items, inline: [:], client: c, since: since, hold: hold)
            if canApply { settings.applyWatermark = nil }
        } catch {
            setMessage("목록 조회 실패: \(error)")
            Log.error("catch-up list: \(error)")
        }
    }

    /// 적용이 다시 켜졌을 때(일시정지 만료·해제, 받기 on) 꺼져 있던 동안의 항목을 로컬 우선 규칙으로 판단한다 (D-60).
    private func resumeIfNeeded() {
        guard let wm = settings.applyWatermark, let c = client, connection == .connected else { return }
        Log.info("apply resumed — catch-up since \(wm)")
        Task { [weak self] in await self?.catchUp(c, since: wm) }
    }

    /// `since`: 새 항목으로 볼 하한 (기본 last_seq). `hold`: 로컬 우선·pending 때문에 이번에는 적용하지 않음.
    private func handle(_ items: [ServerItem], inline: [String: [UInt8]?], client c: ServerClient,
                        since: Int? = nil, apply: Bool = true, hold: Bool = false) async {
        guard let last = settings.lastSeq else { return }
        var candidates: [CatchUpCandidate] = []
        for it in items {
            guard let r = try? await c.receive(it, withBody: false) else { continue }   // 복호화 실패는 폐기 (6.3-5)
            let kinds = (r.header["kinds"] as? [String]) ?? []
            candidates.append(CatchUpCandidate(seq: it.seq, deviceId: it.deviceId, purged: it.purged, kinds: kinds))
            addRecent(it, header: r)
        }
        if apply && !canApply && settings.applyWatermark == nil && candidates.contains(where: { $0.seq > last }) {
            settings.applyWatermark = last   // D-60: 적용이 꺼진 동안 도착한 항목을 재개 시 다시 본다
        }
        let plan = planCatchUp(items: candidates, lastSeq: since ?? last, selfDevice: settings.deviceId,
                               canApply: apply && canApply, allowedKinds: config.allowedKinds, holdLocal: hold)
        settings.lastSeq = max(last, plan.newLastSeq)
        onChange()
        Log.info("handled \(items.count) item(s) lastSeq=\(settings.lastSeq ?? -1) target=\(plan.target.map { String($0.seq) } ?? "-")\(hold ? " (held: local newer or pending)" : "")")
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

    /// 원격 항목을 받아 클립보드에 적용하고, 이미지/파일이면 캐시한 뒤 서버 본문을 지운다 (D-61, D-62).
    func apply(_ it: ServerItem, inline: [UInt8]?, client c: ServerClient) async {
        let large = inline == nil && it.bodySize > 1 << 20
        if large { busy = "받는 중…"; onChange() }
        defer { if large { busy = nil; onChange() } }
        do {
            let r = try await c.receive(it, inlineBody: inline, withBody: true)
            if Task.isCancelled { return }
            guard let entries = r.entries else { setMessage("서버에서 삭제된 항목입니다"); return }
            guard await write(entries, id: it.id, cacheImage: true) else { return }
            Log.info("applied seq=\(it.seq) id=\(it.id) kinds=\(((r.header["kinds"] as? [String]) ?? []).joined(separator: ","))")
            settings.lastAppliedSeq = max(settings.lastAppliedSeq ?? 0, it.seq)
            if entries.contains(where: { $0.type == 3 || $0.type == 4 }) { deleteAfterReceipt(it.id, client: c) }
        } catch {
            if !Task.isCancelled { setMessage("적용 실패: \(error)"); Log.error("apply seq=\(it.seq): \(error)") }
        }
    }

    /// 엔트리를 클립보드에 쓴다. 파일은 캐시에 다 쓴 뒤 URL로, 이미지는 PNG+TIFF(+`cacheImage`면 번들을 캐시), 텍스트는 D-49.
    /// 성공하면 true. 쓴 내용은 우리 것이므로 링과 changeCount를 기록한다 (D-24, D-58).
    private func write(_ entries: [BundleEntry], id: String, cacheImage: Bool) async -> Bool {
        let files = entries.filter { $0.type == 4 }
        if !files.isEmpty {
            // 캐시에 모두 쓴 뒤 클립보드에 기록한다 (붙여넣기 시점에 파일이 완성돼 있어야 함, D-56). 이름은 D-57로 정리.
            let written = await Task.detached { () -> (urls: [URL], hash: String)? in
                let names = uniqueFileNames(files.map(\.name))
                let dir = SyncEngine.cacheDir(id)
                do {
                    try? FileManager.default.removeItem(at: dir)
                    try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                    var urls: [URL] = []
                    for (name, f) in zip(names, files) {
                        let url = dir.appendingPathComponent(name, isDirectory: false)
                        guard url.deletingLastPathComponent().standardizedFileURL == dir.standardizedFileURL else { return nil }   // 경로 탈출 방지(이중 확인)
                        try Data(f.data).write(to: url)
                        urls.append(url)
                    }
                    return (urls, filesHash(zip(names, files).map { ($0.0, $0.1.data) }))
                } catch { return nil }
            }.value
            if Task.isCancelled { return false }
            guard let written else { setMessage("파일을 캐시에 쓰지 못했습니다", notify: true); return false }
            ring.add(written.hash)
            lastChangeCount = PasteboardIO.writeFiles(written.urls, itemId: id)
        } else if let png = entries.first(where: { $0.type == 3 }).map({ Data($0.data) }) {
            // 픽셀 해시(D-54), TIFF 변환, 번들 캐시(D-62)는 메인 스레드 밖에서
            let (hash, tiff) = await Task.detached { () -> (String?, Data?) in
                if cacheImage, let bundle = try? encodeBundle(entries) {
                    let dir = SyncEngine.cacheDir(id)
                    try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                    try? Data(bundle).write(to: dir.appendingPathComponent("bundle.csb1"))
                }
                return (imagePixelHash(png), tiffData(fromPNG: png))
            }.value
            if Task.isCancelled { return false }
            if let hash { ring.add(hash) }
            lastChangeCount = PasteboardIO.write(entries: entries, tiff: tiff, itemId: id)
        } else {
            let plain = entries.first { $0.type == 1 }.map { String(decoding: $0.data, as: UTF8.self) }
            let html = entries.first { $0.type == 2 }.map { String(decoding: $0.data, as: UTF8.self) }
            let hash = contentHash(plainText: plain, html: html)
            if let hash { ring.add(hash) }   // 되돌아오는 변경을 업로드하지 않도록 (D-24)
            if let hash, hash == PasteboardIO.currentTextHash() {
                Log.info("apply skipped (same content) id=\(id)")   // D-49 (텍스트만)
                lastChangeCount = NSPasteboard.general.changeCount
            } else {
                lastChangeCount = PasteboardIO.write(entries: entries, itemId: id)
            }
        }
        markOwn(lastChangeCount)
        return true
    }

    /// 수신 후 삭제 (D-61): 404/410은 완료, 그 밖의 실패는 로그만 (서버 24h 만료가 안전망).
    private func deleteAfterReceipt(_ id: String, client c: ServerClient) {
        Task {
            do {
                try await c.deleteBody(id: id)
                Log.info("deleted body id=\(id)")
            } catch ServerError.http(let s, _) where s == 404 || s == 410 {
                Log.info("delete body id=\(id): already gone (\(s))")
            } catch {
                Log.error("delete body id=\(id): \(error)")
            }
        }
    }

    /// 최근 항목 메뉴에서 선택: 서버에 본문이 있으면 받아서 적용(미디어면 이후 삭제), 없으면 캐시에서 복원 (6.3-6, D-62).
    func restore(_ item: RecentItem) {
        guard let c = client else { return }
        Task { [weak self] in
            guard let self else { return }
            if !item.purged, let list = try? await c.list(since: max(0, item.seq - 1)),
               let it = list.first(where: { $0.id == item.id }), !it.purged {
                await self.apply(it, inline: nil, client: c)
                return
            }
            if self.hasCache(item.id) { await self.restoreFromCache(item.id); return }
            self.setMessage("서버에서 삭제됨")
        }
    }

    private func restoreFromCache(_ id: String) async {
        let dir = Self.cacheDir(id)
        try? FileManager.default.setAttributes([.modificationDate: Date()], ofItemAtPath: dir.path)   // LRU
        let bundleURL = dir.appendingPathComponent("bundle.csb1")
        if let data = try? Data(contentsOf: bundleURL), let entries = try? decodeBundle([UInt8](data)) {
            _ = await write(entries, id: id, cacheImage: false)
        } else {
            let urls = ((try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.isRegularFileKey])) ?? [])
                .filter { (try? $0.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true }
                .sorted { $0.lastPathComponent < $1.lastPathComponent }
            guard !urls.isEmpty else { setMessage("캐시가 비어 있습니다"); return }
            lastChangeCount = PasteboardIO.writeFiles(urls, itemId: id)
            markOwn(lastChangeCount)
        }
        Log.info("restored from cache id=\(id)")
    }

    // MARK: 캐시 정리 (D-62)

    private func cleanupCache() {
        // 현재 클립보드가 우리 항목이면 그 폴더는 지우지 않는다 (붙여넣기할 파일이 사라지지 않도록)
        let protectedId = NSPasteboard.general.pasteboardItems?.first?.data(forType: PasteboardIO.markerType).map { hexEncode($0) }
        Task.detached {
            let fm = FileManager.default
            let root = SyncEngine.cacheRoot
            let keys: [URLResourceKey] = [.isSymbolicLinkKey, .isDirectoryKey, .contentModificationDateKey]
            guard let children = try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: keys) else { return }
            var entries: [CacheEntry] = []
            for dir in children {
                guard isCacheId(dir.lastPathComponent),
                      let v = try? dir.resourceValues(forKeys: Set(keys)),
                      v.isSymbolicLink != true, v.isDirectory == true else { continue }   // 직계, 32자 hex, 심볼릭 링크 아님
                let files = (try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.fileSizeKey, .isRegularFileKey])) ?? []
                let bytes = files.reduce(0) { $0 + ((try? $1.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) }
                entries.append(CacheEntry(id: dir.lastPathComponent, modified: v.contentModificationDate ?? .distantPast, bytes: bytes))
            }
            let delete = planCacheCleanup(entries, now: Date(), protectedId: protectedId)
            for id in delete { try? fm.removeItem(at: SyncEngine.cacheDir(id)) }
            if !delete.isEmpty { Log.info("cache cleanup removed \(delete.count) item(s)") }
        }
    }

    // MARK: 감시 / 송신

    private func poll() {
        let applyNow = canApply
        if applyNow && !wasApplyEnabled { resumeIfNeeded() }   // 일시정지 만료·해제, 받기 on (D-60)
        wasApplyEnabled = applyNow

        let pb = NSPasteboard.general
        guard pb.changeCount != lastChangeCount else { return }
        let observed = pb.changeCount
        lastChangeCount = observed
        guard canSend, let c = client else { return }   // 꺼져 있으면 보내지 않고 pending도 만들지 않는다 (6.2-3)

        let shape = PasteboardIO.shape(pb)
        if shape.own { markOwn(observed); return }
        switch classify(hasFiles: shape.hasFiles, hasImage: shape.hasImage, hasText: shape.hasText, config: config) {
        case .ignore:
            return   // 타입 off 등은 조용히 무시 (4.5)
        case .files:
            let urls = PasteboardIO.readFileURLs(pb)
            if !urls.isEmpty { sendFiles(urls, observed: observed, client: c) }
        case .text:
            guard let content = PasteboardIO.readText(pb) else { return }
            sendText(content, observed: observed, client: c)
        case .image(let withText):
            guard let img = PasteboardIO.readImage(pb) else { return }
            sendImage(img, text: withText ? PasteboardIO.readText(pb) : nil, observed: observed, client: c)
        }
    }

    private func sendText(_ content: PasteboardIO.Content, observed: Int, client c: ServerClient) {
        guard let hash = contentHash(plainText: content.plain, html: content.html) else { return }
        if ring.contains(hash) { markOwn(observed); return }   // 방금 주고받은 내용 = 우리 것
        ring.add(hash)
        let (entries, kinds) = textEntries(content)
        guard entries.reduce(0, { $0 + $1.data.count }) <= VaultConfig.textLimit else {
            setMessage("1 MiB를 넘는 텍스트는 보내지 않습니다", notify: true); return
        }
        guard let prepared = try? c.prepare(entries: entries, kinds: kinds, previewText: content.plain) else { return }
        enqueue(Outgoing(item: prepared, kinds: kinds, preview: previewOf(content.plain ?? ""), observed: observed), client: c)
    }

    private func textEntries(_ content: PasteboardIO.Content) -> ([BundleEntry], [String]) {
        var entries: [BundleEntry] = []
        var kinds: [String] = []
        if let p = content.plain { entries.append(BundleEntry(type: 1, name: "", data: [UInt8](p.utf8))); kinds.append("text") }
        if let h = content.html { entries.append(BundleEntry(type: 2, name: "", data: [UInt8](h.utf8))); kinds.append("html") }
        return (entries, kinds)
    }

    /// 이미지: TIFF→PNG 변환·픽셀 해시·봉인은 메인 스레드 밖에서 한다 (큰 이미지로 메뉴가 멈추지 않도록).
    private func sendImage(_ img: (data: Data, isPNG: Bool), text: PasteboardIO.Content?, observed: Int, client c: ServerClient) {
        let limit = config.maxMediaBytes
        Task { [weak self] in
            let prep = await Task.detached { () -> (png: Data, hash: String, size: (w: Int, h: Int))? in
                guard let png = img.isPNG ? img.data : pngData(fromImageData: img.data),
                      let size = pngSize([UInt8](png)), let hash = imagePixelHash(png) else { return nil }
                return (png, hash, size)
            }.value
            guard let self else { return }
            guard let prep else { Log.error("image conversion failed"); return }
            if self.ring.contains(prep.hash) { self.markOwn(observed); return }
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
            self.enqueue(Outgoing(item: prepared, kinds: kinds, preview: text?.plain.map(previewOf) ?? "이미지 \(prep.size.w)×\(prep.size.h)",
                                  observed: observed), client: c)
        }
    }

    /// 파일: 폴더·패키지 검사와 크기 합계 검사를 **읽기 전에** 하고, 읽기·해시·봉인은 메인 스레드 밖에서 (D-56).
    private func sendFiles(_ urls: [URL], observed: Int, client c: ServerClient) {
        let limit = config.maxMediaBytes
        Task { [weak self] in
            enum Outcome { case folder, tooLarge(Int), unreadable(String), ok([(name: String, data: [UInt8])]) }
            let outcome = await Task.detached { () -> Outcome in
                var total = 0
                for u in urls {
                    let v = try? u.resourceValues(forKeys: [.isDirectoryKey, .isPackageKey, .isRegularFileKey, .fileSizeKey])
                    if v?.isDirectory == true || v?.isPackage == true { return .folder }
                    guard v?.isRegularFile == true else { return .unreadable(u.lastPathComponent) }
                    total += v?.fileSize ?? 0
                }
                if total > limit { return .tooLarge(total) }
                var files: [(name: String, data: [UInt8])] = []
                for u in urls {
                    guard let d = try? Data(contentsOf: u) else { return .unreadable(u.lastPathComponent) }
                    files.append((u.lastPathComponent.precomposedStringWithCanonicalMapping, [UInt8](d)))   // D-25: NFC
                }
                return .ok(files)
            }.value
            guard let self else { return }
            switch outcome {
            case .folder:
                self.setMessage("폴더나 패키지가 포함된 복사는 보내지 않습니다", notify: true)   // D-31
            case .tooLarge(let total):
                self.setMessage("파일이 \(limit >> 20) MB를 넘어 보내지 않았습니다 (\(String(format: "%.1f", Double(total) / 1048576)) MB)", notify: true)
            case .unreadable(let name):
                self.setMessage("파일을 읽지 못했습니다: \(name)", notify: true)
            case .ok(let files):
                let hash = filesHash(files)
                if self.ring.contains(hash) { self.markOwn(observed); return }
                self.ring.add(hash)
                let entries = files.map { BundleEntry(type: 4, name: $0.name, data: $0.data) }
                let meta = files.map { (name: $0.name, size: $0.data.count) }
                let preview = files.map(\.name).joined(separator: ", ")
                let prepared = await Task.detached { try? c.prepare(entries: entries, kinds: ["files"], previewText: preview, files: meta) }.value
                guard let prepared else { return }
                self.enqueue(Outgoing(item: prepared, kinds: ["files"], preview: preview, observed: observed), client: c)
            }
        }
    }

    private func enqueue(_ out: Outgoing, client c: ServerClient) {
        let prev = sendChain
        sendChain = Task { [weak self] in
            await prev?.value
            await self?.upload(out, client: c)
        }
    }

    /// 재연결 직후 pending을 송신 큐로 올리고 끝날 때까지 기다린다. 있었으면 true (그 catch-up은 적용하지 않음, D-59).
    private func flushPending() async -> Bool {
        guard let p = pending, let c = client else { return false }
        pending = nil
        Log.info("uploading pending id=\(p.item.id)")
        enqueue(p, client: c)
        await sendChain?.value
        return true
    }

    /// 지수 백오프로 최대 3회. 같은 PreparedItem을 재전송하므로 서버에 중복 항목이 생기지 않는다 (idempotent).
    /// 전송 계층 실패로 끝나면 pending으로 보관한다 (D-59).
    private func upload(_ out: Outgoing, client c: ServerClient) async {
        let item = out.item
        let large = item.bodySize > 1 << 20
        if large { busy = "보내는 중…"; onChange() }
        defer { if large { busy = nil; onChange() } }
        var delay = 1.0
        for attempt in 1...3 {
            do {
                let seq = try await c.upload(item)
                Log.info("sent seq=\(seq) id=\(item.id) kinds=\(out.kinds.joined(separator: ",")) bytes=\(item.bodySize)")
                // 서버는 업로더의 소켓에 이벤트를 보내지 않으므로 내가 보낸 항목을 최근 항목에 직접 추가한다.
                recent.append(RecentItem(seq: seq, id: item.id, deviceId: settings.deviceId, kinds: out.kinds, preview: out.preview,
                                         createdAt: item.createdAt, purged: false))
                recent.sort { $0.seq > $1.seq }
                if recent.count > 20 { recent.removeLast(recent.count - 20) }
                if let p = pending, p.observed <= out.observed {   // 더 새 항목이 올라갔으니 옛 pending은 버린다
                    Log.info("pending id=\(p.item.id) superseded by id=\(item.id)")
                    pending = nil
                }
                markOwn(out.observed)
                setMessage(nil); return
            } catch {
                Log.error("upload id=\(item.id) attempt=\(attempt): \(error)")
                let transport = isTransportFailure(error)
                if attempt == 3 || !transport {
                    if transport {
                        if (pending?.observed ?? Int.min) <= out.observed { pending = out }
                        Log.info("kept as pending id=\(item.id)")
                        setMessage("오프라인: 마지막 복사를 보관했다가 다시 연결되면 보냅니다", notify: !outageAlerted)
                        outageAlerted = true
                    } else {
                        setMessage("전송 실패: \(error)", notify: true)
                    }
                    return
                }
                try? await Task.sleep(for: .seconds(delay)); delay *= 2
            }
        }
    }
}
