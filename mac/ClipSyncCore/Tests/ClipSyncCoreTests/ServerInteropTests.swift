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
}
