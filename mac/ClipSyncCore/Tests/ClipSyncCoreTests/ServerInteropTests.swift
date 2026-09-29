import Foundation
import Testing
@testable import ClipSyncCore

// 서버 상호운용 테스트. CLIPSYNC_URL / CLIPSYNC_PASSPHRASE 가 있을 때만 실행한다 (없으면 통과 처리).
//   CLIPSYNC_URL=https://clipsync-dev.clipboardsync.workers.dev \
//   CLIPSYNC_PASSPHRASE="abacus abdomen abide abnormal abrasion abroad absence" swift test
private struct Env {
    let url: URL
    let passphrase: String
    static func load() -> Env? {
        let e = ProcessInfo.processInfo.environment
        guard let u = e["CLIPSYNC_URL"], let p = e["CLIPSYNC_PASSPHRASE"], let url = URL(string: u) else { return nil }
        return Env(url: url, passphrase: p)
    }
}

private func refDir(file: String = #filePath) -> URL {
    var dir = URL(fileURLWithPath: file).deletingLastPathComponent()
    for _ in 0..<12 {
        let c = dir.appendingPathComponent("protocol/ref/src/cli.ts")
        if FileManager.default.fileExists(atPath: c.path) { return dir.appendingPathComponent("protocol/ref") }
        dir.deleteLastPathComponent()
    }
    fatalError("protocol/ref not found")
}

/// TS 참조 피어(cli.ts)를 실행하고 stdout을 돌려준다.
private func runCLI(_ env: Env, _ args: [String]) throws -> String {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/env")
    p.arguments = ["node", "src/cli.ts"] + args
    p.currentDirectoryURL = refDir()
    var environment = ProcessInfo.processInfo.environment
    environment["CLIPSYNC_URL"] = env.url.absoluteString
    environment["CLIPSYNC_PASSPHRASE"] = env.passphrase
    p.environment = environment
    let out = Pipe()
    p.standardOutput = out
    try p.run()
    let data = out.fileHandleForReading.readDataToEndOfFile()
    p.waitUntilExit()
    return String(decoding: data, as: UTF8.self)
}

private func makeClient(_ env: Env) throws -> ServerClient {
    try ServerClient(baseURL: env.url, keys: deriveKeys(passphrase: env.passphrase))
}

@Suite struct ServerInteropTests {
    @Test func bearerMatchesNodeBase64URL() throws {
        // test-vectors "ascii"의 auth_token → Node의 base64url 결과와 같아야 한다 (패딩 없음, URL-safe)
        let keys = try deriveKeys(passphrase: "abacus abdomen abide abnormal abrasion abroad absence")
        #expect(base64URL(keys.authToken) == "uNwMUkQX9SphZQHLHXVX8C9ios7PNtnbUYYnrsRk_Ew")
    }

    @Test func deviceIdIsLowercaseHex() throws {
        let c = try ServerClient(baseURL: URL(string: "https://x.test")!, keys: deriveKeys(passphrase: "abacus abdomen abide abnormal abrasion abroad absence"))
        #expect(c.deviceId.count == 32)
        #expect(c.deviceId.allSatisfy { "0123456789abcdef".contains($0) })
    }

    @Test func parseEventCases() throws {
        if case .hello(let seq, let cfg)? = ServerClient.parseEvent(#"{"t":"hello","seq":7,"config":null}"#) {
            #expect(seq == 7); #expect(cfg == nil)
        } else { Issue.record("hello") }
        if case .bodyPurged(let id)? = ServerClient.parseEvent(#"{"t":"body_purged","id":"ab"}"#) { #expect(id == "ab") } else { Issue.record("purged") }
        #expect(ServerClient.parseEvent(#"{"t":"unknown"}"#) == nil)
    }

    @Test func swiftSendsCLIDecrypts() async throws {
        guard let env = Env.load() else { return }
        let text = "swift→cli 한글 🎉 \(UUID().uuidString)"
        let sent = try await makeClient(env).sendText(text)
        let out = try runCLI(env, ["list"])
        #expect(out.contains(sent.id), "cli list must decrypt the Swift item")
        #expect(out.contains("swift→cli 한글 🎉"))
    }

    @Test func cliSendsSwiftDecrypts() async throws {
        guard let env = Env.load() else { return }
        let text = "cli→swift 한글 🎉 \(UUID().uuidString)"
        let out = try runCLI(env, ["send", text])
        let id = try #require((try JSONSerialization.jsonObject(with: Data(out.utf8)) as? [String: Any])?["id"] as? String)
        let client = try makeClient(env)
        let item = try #require(try await client.list().first { $0.id == id })
        let r = try await client.receive(item)
        #expect(String(decoding: r.entries![0].data, as: UTF8.self) == text)
        #expect((r.header["kinds"] as? [String]) == ["text"])
    }

    @Test func webSocketDeliversInlineAndPong() async throws {
        guard let env = Env.load() else { return }
        let receiver = try makeClient(env), sender = try makeClient(env)
        let events = receiver.events()
        var stream = events.makeAsyncIterator()
        guard case .hello? = await stream.next() else { Issue.record("expected hello"); return }
        let text = "ws \(UUID().uuidString)"
        let sent = try await sender.sendText(text)
        var got: (ServerItem, [UInt8]?)?
        while let ev = await stream.next() {
            if case .item(let it, let inline) = ev, it.id == sent.id { got = (it, inline); break }
        }
        withExtendedLifetime(events) {}
        let (item, inline) = try #require(got)
        #expect(inline != nil)
        let r = try await receiver.receive(item, inlineBody: inline)
        #expect(String(decoding: r.entries![0].data, as: UTF8.self) == text)
        try await receiver.deleteBody(id: item.id)
    }

    @Test func wrongPassphraseIs401() async throws {
        guard let env = Env.load() else { return }
        let bad = try ServerClient(baseURL: env.url, keys: deriveKeys(passphrase: "wrong passphrase not registered"))
        await #expect(throws: ServerError.http(status: 401, op: "list")) { _ = try await bad.list() }
    }

    /// D-50: TS가 쓴 설정을 Swift가 열고, Swift가 쓴 설정을 TS가 연다 (version AAD 포함).
    @Test func configCrossReadWrite() async throws {
        guard let env = Env.load() else { return }
        let client = try makeClient(env)
        _ = try runCLI(env, ["config-set", "images=on", "files=off", "max=10"])
        let blob = try #require(try await client.getConfig())
        #expect(try openConfig(blob, key: client.keys.encKey) == VaultConfig(images: true, files: false, maxMediaBytes: 10 << 20))
        let (v, cfg) = try await client.writeConfig { $0.images = false; $0.maxMediaBytes = 20 << 20 }
        #expect(v == blob.version + 1)
        #expect(cfg == .failClosed)
        let out = try runCLI(env, ["config-get"])
        #expect(out.contains("\"version\":\(v)"))
        #expect(out.contains("\"images\":false"))
    }

    /// R2 경로(>1.25 MiB)를 타는 이미지 번들을 Swift가 보내고 TS가 복호화한다.
    @Test func largeImageBundleCrossesR2() async throws {
        guard let env = Env.load() else { return }
        var png = [UInt8](MediaTests.samplePNG())
        png += [UInt8](repeating: 0, count: 2 << 20)   // IHDR는 그대로, 뒤에 패딩(크기 확인용)
        let sent = try await makeClient(env).send(entries: [BundleEntry(type: 3, name: "", data: png)], kinds: ["image"])
        let out = try runCLI(env, ["list"])
        let line = try #require(out.split(separator: "\n").first { $0.contains(sent.id) })
        #expect(line.contains("\"bytes\":\(png.count)"))
    }

    /// D-28: 서버가 "ping"이 아닌 텍스트에는 응답하지 않으므로 "xping"을 보내면 pong이 끊긴 상황을 재현한다.
    @Test func silentConnectionIsClosedAfterPongTimeout() async throws {
        guard let env = Env.load() else { return }
        let events = try makeClient(env).events(pingInterval: .seconds(1), pongTimeout: .seconds(3), pingPayload: "xping")
        var it = events.makeAsyncIterator()
        guard case .hello? = await it.next() else { Issue.record("expected hello"); return }
        let start = ContinuousClock.now
        var closed: String?
        while let ev = await it.next() { if case .closed(let why) = ev { closed = why; break } }
        #expect(closed?.contains("no pong") == true)
        #expect(ContinuousClock.now - start < .seconds(10))
        withExtendedLifetime(events) {}
    }

    /// 대조군: 올바른 "ping"이면 같은 짧은 타임아웃에서도 연결이 유지된다.
    @Test func properPingKeepsConnectionAlive() async throws {
        guard let env = Env.load() else { return }
        let events = try makeClient(env).events(pingInterval: .seconds(1), pongTimeout: .seconds(3))
        let ended = EndedFlag()
        let watcher = Task {
            var it = events.makeAsyncIterator()
            while let ev = await it.next() { if case .closed = ev { break } }
            await ended.set()
        }
        try await Task.sleep(for: .seconds(7))   // 타임아웃(3초)의 두 배 넘게 유지되어야 한다
        let wasClosed = await ended.value
        watcher.cancel()
        #expect(!wasClosed, "ping/pong should keep the connection open past the timeout")
    }
}

private actor EndedFlag {
    private(set) var value = false
    func set() { value = true }
}
