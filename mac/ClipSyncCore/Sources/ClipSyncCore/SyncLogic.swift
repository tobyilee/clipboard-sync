import CryptoKit
import Foundation

// MARK: - 해시 정규화 / dedupe (spec 6.4, D-24, D-41)

/// 텍스트/HTML 해시 입력용 정규화: CRLF→LF, 끝의 NUL 제거, NFC. (본문 자체는 바꾸지 않는다, 해시 계산에만 쓴다.)
public func normalizeForHash(_ s: String) -> String {
    var t = s.replacingOccurrences(of: "\r\n", with: "\n")
    while t.hasSuffix("\0") { t.removeLast() }
    return t.precomposedStringWithCanonicalMapping
}

/// D-41: 정규화한 plain text가 있으면 그것을, 없으면 정규화한 HTML을 해시한다.
public func contentHash(plainText: String?, html: String?) -> String? {
    guard let source = plainText ?? html else { return nil }
    return hexEncode(SHA256.hash(data: Data(normalizeForHash(source).utf8)))
}

/// 마지막 송·수신 항목 5개의 해시를 60초간 보관해 같은 해시의 변경을 업로드하지 않는다 (필수 방어).
public struct RecentHashRing: Sendable {
    public let capacity: Int
    public let ttl: TimeInterval
    private var entries: [(hash: String, at: Date)] = []

    public init(capacity: Int = 5, ttl: TimeInterval = 60) {
        self.capacity = capacity
        self.ttl = ttl
    }

    public mutating func add(_ hash: String, now: Date = Date()) {
        prune(now)
        entries.removeAll { $0.hash == hash }
        entries.append((hash, now))
        if entries.count > capacity { entries.removeFirst(entries.count - capacity) }
    }

    public mutating func contains(_ hash: String, now: Date = Date()) -> Bool {
        prune(now)
        return entries.contains { $0.hash == hash }
    }

    private mutating func prune(_ now: Date) {
        entries.removeAll { now.timeIntervalSince($0.at) > ttl }
    }
}

// MARK: - HTML (D-39, D-40)

/// `NSAttributedString`이 내보낸 전체 HTML 문서에서 `<head>`의 `<style>` 블록을 `<body>` 내용 앞에 붙인 fragment를 만든다.
/// `<body>`만 취하면 서식(클래스 스타일)이 사라지므로 style을 함께 옮긴다.
public func htmlFragment(fromDocument doc: String) -> String {
    let opts: NSRegularExpression.Options = [.caseInsensitive, .dotMatchesLineSeparators]
    let ns = doc as NSString
    let full = NSRange(location: 0, length: ns.length)
    var styles: [String] = []
    if let re = try? NSRegularExpression(pattern: "<style\\b[^>]*>.*?</style>", options: opts) {
        styles = re.matches(in: doc, range: full).map { ns.substring(with: $0.range) }
    }
    var body = doc
    if let re = try? NSRegularExpression(pattern: "<body\\b[^>]*>(.*)</body>", options: opts),
       let m = re.firstMatch(in: doc, range: full), m.numberOfRanges > 1 {
        body = ns.substring(with: m.range(at: 1))
    }
    return (styles.joined() + body).trimmingCharacters(in: .whitespacesAndNewlines)
}

/// 원격 HTML을 pasteboard에 쓸 때 charset을 보장한다 (한글·이모지 깨짐 방지, D-40).
public func htmlForApply(_ html: String) -> String {
    html.range(of: "charset", options: .caseInsensitive) != nil ? html : "<meta charset='utf-8'>" + html
}

// MARK: - catch-up 선택 (spec 6.3, D-42)

public struct CatchUpCandidate: Equatable, Sendable {
    public var seq: Int
    public var deviceId: String
    public var purged: Bool
    public var kinds: [String]
    public init(seq: Int, deviceId: String, purged: Bool, kinds: [String]) {
        self.seq = seq; self.deviceId = deviceId; self.purged = purged; self.kinds = kinds
    }
}

public struct CatchUpPlan: Equatable, Sendable {
    /// 처리 후 저장할 last_seq (새 항목이 없으면 그대로)
    public var newLastSeq: Int
    /// 적용할 항목: 후보 중 가장 높은 seq 하나 (FR-6). 없으면 nil.
    public var target: CatchUpCandidate?
}

/// `seq <= lastSeq`와 자기 기기 항목은 건너뛴다. 종류가 허용되고 purged가 아닌 항목 중 seq가 가장 높은 하나만 적용 대상이다.
/// `holdLocal`: 로컬 우선 규칙(D-58)이나 pending(D-59) 때문에 이번 catch-up에서는 원격 항목을 적용하지 않는다 (목록만 갱신).
public func planCatchUp(items: [CatchUpCandidate], lastSeq: Int, selfDevice: String,
                        canApply: Bool, allowedKinds: Set<String>, holdLocal: Bool = false) -> CatchUpPlan {
    let fresh = items.filter { $0.seq > lastSeq }
    let newLast = fresh.map(\.seq).max() ?? lastSeq
    guard canApply, !holdLocal else { return CatchUpPlan(newLastSeq: newLast, target: nil) }
    let target = fresh
        .filter { $0.deviceId != selfDevice && !$0.purged && !$0.kinds.isEmpty && Set($0.kinds).isSubset(of: allowedKinds) }
        .max { $0.seq < $1.seq }
    return CatchUpPlan(newLastSeq: newLast, target: target)
}

// MARK: - 캐시 정리 (D-62)

public struct CacheEntry: Equatable, Sendable {
    public var id: String
    public var modified: Date
    public var bytes: Int
    public init(id: String, modified: Date, bytes: Int) { self.id = id; self.modified = modified; self.bytes = bytes }
}

/// 캐시 폴더 이름으로 쓸 수 있는 item id (소문자 hex 32자). 정리는 이 이름의 폴더만 건드린다.
public func isCacheId(_ name: String) -> Bool {
    name.utf8.count == 32 && name.utf8.allSatisfy { (0x30...0x39).contains($0) || (0x61...0x66).contains($0) }
}

/// 지울 id 목록: 24h 넘은 것, 그다음 오래된 순으로 총량이 한도 이하가 될 때까지. `protectedId`(현재 클립보드 항목)는 지우지 않는다.
public func planCacheCleanup(_ entries: [CacheEntry], now: Date, maxAge: TimeInterval = 24 * 3600,
                             maxBytes: Int = 200 << 20, protectedId: String?) -> [String] {
    let candidates = entries.filter { isCacheId($0.id) }
    var delete = Set(candidates.filter { $0.id != protectedId && now.timeIntervalSince($0.modified) > maxAge }.map(\.id))
    var remaining = candidates.filter { !delete.contains($0.id) }.sorted { $0.modified < $1.modified }
    var total = remaining.reduce(0) { $0 + $1.bytes }
    while total > maxBytes, let i = remaining.firstIndex(where: { $0.id != protectedId }) {
        total -= remaining[i].bytes
        delete.insert(remaining[i].id)
        remaining.remove(at: i)
    }
    return candidates.map(\.id).filter { delete.contains($0) }
}

/// D-59: pending으로 보관할 실패인가 (전송 계층: 연결·타임아웃·5xx). 409/411/413 등 요청 자체의 문제는 아니다.
public func isTransportFailure(_ error: Error) -> Bool {
    if error is URLError { return true }
    if case ServerError.http(let status, _) = error { return status >= 500 || status == 0 }
    return false
}
