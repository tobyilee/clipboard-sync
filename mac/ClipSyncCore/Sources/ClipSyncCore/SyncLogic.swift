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
public func planCatchUp(items: [CatchUpCandidate], lastSeq: Int, selfDevice: String,
                        canApply: Bool, allowedKinds: Set<String>) -> CatchUpPlan {
    let fresh = items.filter { $0.seq > lastSeq }
    let newLast = fresh.map(\.seq).max() ?? lastSeq
    guard canApply else { return CatchUpPlan(newLastSeq: newLast, target: nil) }
    let target = fresh
        .filter { $0.deviceId != selfDevice && !$0.purged && !$0.kinds.isEmpty && Set($0.kinds).isSubset(of: allowedKinds) }
        .max { $0.seq < $1.seq }
    return CatchUpPlan(newLastSeq: newLast, target: target)
}
