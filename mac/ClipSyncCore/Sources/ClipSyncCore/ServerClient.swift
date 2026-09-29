// 서버 API(spec 5장) 클라이언트. protocol/ref/src/peer.ts 와 같은 동작을 Swift로 옮긴 것.
import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public enum ServerError: Error, Equatable {
    case http(status: Int, op: String)
    case badResponse(String)
}

public struct ServerItem: Codable, Equatable, Sendable {
    public var seq: Int
    public var id: String          // 소문자 hex (D-35)
    public var deviceId: String    // 소문자 hex
    public var createdAt: UInt64
    public var header: String      // 표준 base64
    public var bodySize: Int
    public var purged: Bool

    enum CodingKeys: String, CodingKey {
        case seq, id, header, purged
        case deviceId = "device_id", createdAt = "created_at", bodySize = "body_size"
    }

    /// WS `item` 이벤트에는 `purged`가 없다 (방금 커밋된 항목이므로 false).
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        seq = try c.decode(Int.self, forKey: .seq)
        id = try c.decode(String.self, forKey: .id)
        deviceId = try c.decode(String.self, forKey: .deviceId)
        createdAt = try c.decode(UInt64.self, forKey: .createdAt)
        header = try c.decode(String.self, forKey: .header)
        bodySize = try c.decode(Int.self, forKey: .bodySize)
        purged = try c.decodeIfPresent(Bool.self, forKey: .purged) ?? false
    }
}

public struct ConfigBlob: Equatable, Sendable {
    public var version: Int
    public var blob: [UInt8]
}

public enum ServerEvent: Sendable {
    case hello(seq: Int, config: ConfigBlob?)
    case item(ServerItem, inlineBody: [UInt8]?)
    case bodyPurged(id: String)
    case config(ConfigBlob)
    case closed(String?)
}

public struct ReceivedItem: Sendable {
    public var seq: Int
    public var id: String
    public var deviceId: String
    public var header: [String: Sendable]
    /// nil이면 본문이 삭제됨(410/404) 또는 요청하지 않음
    public var entries: [BundleEntry]?
}

public func base64URL(_ bytes: [UInt8]) -> String {
    Data(bytes).base64EncodedString()
        .replacingOccurrences(of: "+", with: "-")
        .replacingOccurrences(of: "/", with: "_")
        .replacingOccurrences(of: "=", with: "")
}

public func randomBytes(_ n: Int) throws -> [UInt8] {
    var out = [UInt8](repeating: 0, count: n)
    guard SecRandomCopyBytes(kSecRandomDefault, n, &out) == errSecSuccess else { throw ProtocolError.randomFailure }
    return out
}

public final class ServerClient: Sendable {
    public let baseURL: URL
    public let keys: Keys
    public let deviceId: String   // 소문자 hex 32자
    private let bearer: String
    private let session: URLSession

    public init(baseURL: URL, keys: Keys, deviceId: String? = nil, session: URLSession? = nil) throws {
        self.baseURL = baseURL
        self.keys = keys
        self.deviceId = try deviceId ?? uuidHex(randomBytes(16))
        self.bearer = "Bearer " + base64URL(keys.authToken)
        if let session { self.session = session } else {
            let cfg = URLSessionConfiguration.ephemeral
            cfg.timeoutIntervalForRequest = 300   // 대용량 업로드/다운로드 (spec 5.2)
            cfg.timeoutIntervalForResource = 600
            self.session = URLSession(configuration: cfg)
        }
    }

    /// 서버가 요구하는 Authorization 헤더 값 (테스트용).
    public var authorizationHeader: String { bearer }

    private func request(_ path: String, method: String = "GET", headers: [String: String] = [:]) -> URLRequest {
        var r = URLRequest(url: URL(string: baseURL.absoluteString + path)!)
        r.httpMethod = method
        r.setValue(bearer, forHTTPHeaderField: "Authorization")
        for (k, v) in headers { r.setValue(v, forHTTPHeaderField: k) }
        return r
    }

    // MARK: 송신

    /// 2단계 업로드(PUT body → POST commit, 둘 다 idempotent). 재시도는 호출자가 한다.
    public func send(entries: [BundleEntry], kinds: [String], previewText: String? = nil) async throws -> (id: String, seq: Int) {
        let id = try randomBytes(16)
        let dev = hexDecode(deviceId)!
        let createdAt = UInt64(Date().timeIntervalSince1970 * 1000)
        let body = try encodeBundle(entries)
        var header: [String: Any] = ["v": 1, "kinds": kinds, "body_plain_size": body.count]
        if let previewText { header["preview"] = previewOf(previewText) }
        let headerJSON = try JSONSerialization.data(withJSONObject: header)
        let sealedBody = try seal(key: keys.encKey, aad: itemAad(itemId: id, deviceId: dev, createdAtMs: createdAt, part: .body), plaintext: body)
        let sealedHeader = try seal(key: keys.encKey, aad: itemAad(itemId: id, deviceId: dev, createdAtMs: createdAt, part: .header), plaintext: [UInt8](headerJSON))
        let idHex = uuidHex(id)

        let put = request("/v1/items/\(idHex)/body", method: "PUT", headers: ["X-Device-Id": deviceId])
        let (_, putResp) = try await session.upload(for: put, from: Data(sealedBody))
        try expect(putResp, 204, "PUT body")

        let post = request("/v1/items", method: "POST", headers: ["X-Item-Id": idHex, "X-Device-Id": deviceId, "X-Created-At": String(createdAt)])
        let (data, postResp) = try await session.upload(for: post, from: Data(sealedHeader))
        try expect(postResp, 200, "POST commit")
        guard let seq = (try JSONSerialization.jsonObject(with: data) as? [String: Any])?["seq"] as? Int else {
            throw ServerError.badResponse("commit response")
        }
        return (idHex, seq)
    }

    public func sendText(_ text: String) async throws -> (id: String, seq: Int) {
        try await send(entries: [BundleEntry(type: 1, name: "", data: [UInt8](text.utf8))], kinds: ["text"], previewText: text)
    }

    // MARK: 조회 / 수신

    public func list(since: Int = 0) async throws -> [ServerItem] {
        let (data, resp) = try await session.data(for: request("/v1/items?since=\(since)"))
        try expect(resp, 200, "list")
        return try JSONDecoder().decode([ServerItem].self, from: data)
    }

    /// header를 복호화하고, `withBody`이면 본문도 받아 복호화한다.
    public func receive(_ item: ServerItem, inlineBody: [UInt8]? = nil, withBody: Bool = true) async throws -> ReceivedItem {
        let id = hexDecode(item.id)!, dev = hexDecode(item.deviceId)!
        guard let sealedHeader = Data(base64Encoded: item.header) else { throw ServerError.badResponse("header base64") }
        let plain = try open(key: keys.encKey, aad: itemAad(itemId: id, deviceId: dev, createdAtMs: item.createdAt, part: .header), sealed: [UInt8](sealedHeader))
        let header = (try JSONSerialization.jsonObject(with: Data(plain)) as? [String: Sendable]) ?? [:]
        var entries: [BundleEntry]?
        if withBody, !item.purged {
            var sealed = inlineBody
            if sealed == nil {
                let (data, resp) = try await session.data(for: request("/v1/items/\(item.id)/body"))
                let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
                if status == 200 { sealed = [UInt8](data) }
                else if status != 404 && status != 410 { throw ServerError.http(status: status, op: "GET body") }
            }
            if let sealed {
                entries = try decodeBundle(open(key: keys.encKey, aad: itemAad(itemId: id, deviceId: dev, createdAtMs: item.createdAt, part: .body), sealed: sealed))
            }
        }
        return ReceivedItem(seq: item.seq, id: item.id, deviceId: item.deviceId, header: header, entries: entries)
    }

    /// 수신 후 삭제 (D-16).
    public func deleteBody(id: String) async throws {
        let (_, resp) = try await session.data(for: request("/v1/items/\(id)/body", method: "DELETE"))
        try expect(resp, 204, "DELETE body")
    }

    public func getConfig() async throws -> ConfigBlob? {
        let (data, resp) = try await session.data(for: request("/v1/config"))
        let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
        if status == 404 { return nil }
        try expect(resp, 200, "GET config")
        return try Self.parseConfig(try JSONSerialization.jsonObject(with: data))
    }

    private static func parseConfig(_ obj: Any?) throws -> ConfigBlob {
        guard let d = obj as? [String: Any], let v = d["version"] as? Int, let b = d["blob"] as? String, let blob = Data(base64Encoded: b) else {
            throw ServerError.badResponse("config")
        }
        return ConfigBlob(version: v, blob: [UInt8](blob))
    }

    private func expect(_ resp: URLResponse, _ status: Int, _ op: String) throws {
        let s = (resp as? HTTPURLResponse)?.statusCode ?? 0
        if s != status { throw ServerError.http(status: s, op: op) }
    }

    // MARK: WebSocket

    /// 서버 이벤트 스트림. 30초마다 텍스트 "ping"을 보낸다 (제어 프레임 ping은 DO auto-response와 맞지 않는다, D-42).
    /// 스트림을 끝내면(취소 포함) 연결도 닫힌다. 재연결/백오프는 호출자 책임.
    public func events() -> AsyncStream<ServerEvent> {
        var comps = URLComponents(url: baseURL, resolvingAgainstBaseURL: false)!
        comps.scheme = comps.scheme == "https" ? "wss" : "ws"
        comps.path = "/v1/ws"
        comps.queryItems = [URLQueryItem(name: "device_id", value: deviceId)]
        var req = URLRequest(url: comps.url!)
        req.setValue(bearer, forHTTPHeaderField: "Authorization")
        let task = session.webSocketTask(with: req)
        return AsyncStream { continuation in
            let pinger = Task {
                while !Task.isCancelled {
                    try? await Task.sleep(for: .seconds(30))
                    if Task.isCancelled { break }
                    try? await task.send(.string("ping"))
                }
            }
            let reader = Task {
                do {
                    while !Task.isCancelled {
                        let msg = try await task.receive()
                        guard case .string(let s) = msg, s != "pong", let ev = Self.parseEvent(s) else { continue }
                        continuation.yield(ev)
                    }
                } catch {
                    continuation.yield(.closed(String(describing: error)))
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in
                pinger.cancel()
                reader.cancel()
                task.cancel(with: .normalClosure, reason: nil)
            }
            task.resume()
        }
    }

    static func parseEvent(_ s: String) -> ServerEvent? {
        guard let obj = try? JSONSerialization.jsonObject(with: Data(s.utf8)) as? [String: Any], let t = obj["t"] as? String else { return nil }
        switch t {
        case "hello":
            guard let seq = obj["seq"] as? Int else { return nil }
            return .hello(seq: seq, config: try? parseConfig(obj["config"]))
        case "item":
            guard let data = try? JSONSerialization.data(withJSONObject: obj),
                  let item = try? JSONDecoder().decode(ServerItem.self, from: data) else { return nil }
            let inline = (obj["inline_body"] as? String).flatMap { Data(base64Encoded: $0) }.map { [UInt8]($0) }
            return .item(item, inlineBody: inline)
        case "body_purged":
            return (obj["id"] as? String).map { .bodyPurged(id: $0) }
        case "config":
            return (try? parseConfig(obj)).map { .config($0) }
        default:
            return nil
        }
    }
}
