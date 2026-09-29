import Foundation

/// vault 설정 (spec 4.4). 서버는 읽을 수 없고, 송신 측 클라이언트가 강제한다. 없거나 못 받으면 텍스트만 (fail-closed).
public struct VaultConfig: Codable, Equatable, Sendable {
    public var v: Int = 1
    public var images: Bool = false
    public var files: Bool = false
    public var maxMediaBytes: Int = VaultConfig.defaultMediaBytes

    public static let mib = 1 << 20
    public static let defaultMediaBytes = 20 * mib
    public static let mediaSizes = [5 * mib, 10 * mib, 20 * mib, 50 * mib]
    public static let textLimit = 1 * mib
    public static let failClosed = VaultConfig()

    enum CodingKeys: String, CodingKey { case v, images, files, maxMediaBytes = "max_media_bytes" }

    public init(images: Bool = false, files: Bool = false, maxMediaBytes: Int = VaultConfig.defaultMediaBytes) {
        self.images = images
        self.files = files
        self.maxMediaBytes = maxMediaBytes
    }

    /// 허용값(5/10/20/50 MiB)이 아니면 기본값으로 둔다.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        v = try c.decodeIfPresent(Int.self, forKey: .v) ?? 1
        images = try c.decodeIfPresent(Bool.self, forKey: .images) ?? false
        files = try c.decodeIfPresent(Bool.self, forKey: .files) ?? false
        let m = try c.decodeIfPresent(Int.self, forKey: .maxMediaBytes) ?? Self.defaultMediaBytes
        maxMediaBytes = Self.mediaSizes.contains(m) ? m : Self.defaultMediaBytes
    }

    /// D-51: config 키 → header kinds. 수신 허용 종류와 송신 판별이 모두 이 집합을 쓴다.
    public var allowedKinds: Set<String> {
        var s: Set<String> = ["text", "html"]
        if images { s.insert("image") }
        if files { s.insert("files") }
        return s
    }
}

/// D-50: 저장될 version(If-Match+1)의 AAD로 봉인한다.
public func sealConfig(_ config: VaultConfig, key: [UInt8], version: Int) throws -> [UInt8] {
    try seal(key: key, aad: configAad(configVersion: UInt64(version)), plaintext: [UInt8](JSONEncoder().encode(config)))
}

/// 서버가 알려 준 version으로 연다. 실패하면 throw (호출자는 무시하고 캐시 유지).
public func openConfig(_ blob: ConfigBlob, key: [UInt8]) throws -> VaultConfig {
    try JSONDecoder().decode(VaultConfig.self, from: Data(open(key: key, aad: configAad(configVersion: UInt64(blob.version)), sealed: blob.blob)))
}

// MARK: - 송신 판별 (spec 4.5)

public enum SendDecision: Equatable, Sendable {
    /// 조용히 무시 (타입 off 등)
    case ignore
    /// 파일 항목 (파일만 담는다, 파일명 텍스트 폴백 없음)
    case files
    /// 이미지 항목 (+ 남은 텍스트 표현)
    case image(withText: Bool)
    case text
}

/// 표현 존재 여부만으로 판별한다 (꺼진 타입은 내용을 읽지 않기 위해).
public func classify(hasFiles: Bool, hasImage: Bool, hasText: Bool, config: VaultConfig) -> SendDecision {
    if hasFiles { return config.files ? .files : .ignore }   // 4.5-1: 파일명 문자열로 폴백하지 않는다
    if hasImage {
        if config.images { return .image(withText: hasText) }
        return hasText ? .text : .ignore                       // 4.5-2: 이미지 표현만 버린다
    }
    return hasText ? .text : .ignore
}

/// body 평문 한도: 이미지/파일 엔트리가 있으면 max_media_bytes, 없으면 1 MiB.
public func plainSizeLimit(hasMedia: Bool, config: VaultConfig) -> Int {
    hasMedia ? config.maxMediaBytes : VaultConfig.textLimit
}

/// PNG IHDR에서 가로·세로 (header `image {w,h}`용, 디코드하지 않음).
public func pngSize(_ b: [UInt8]) -> (w: Int, h: Int)? {
    let sig: [UInt8] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]
    guard b.count >= 24, Array(b[0..<8]) == sig, Array(b[12..<16]) == Array("IHDR".utf8) else { return nil }
    func u32(_ o: Int) -> Int { Int(b[o]) << 24 | Int(b[o + 1]) << 16 | Int(b[o + 2]) << 8 | Int(b[o + 3]) }
    return (u32(16), u32(20))
}
