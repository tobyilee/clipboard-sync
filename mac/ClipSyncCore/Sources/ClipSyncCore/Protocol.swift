// Swift implementation of protocol v1 (see protocol/PROTOCOL.md).
import CommonCrypto
import CryptoKit
import Foundation

public enum ProtocolError: Error, Equatable {
    case invalidUUID(String)
    case invalidLength(String)
    case tooShort
    case decryptionFailed
    case bundle(String)
    case keyDerivationFailed
    case randomFailure
}

// MARK: - 2. key derivation

private func isProtocolWhitespace(_ cp: UInt32) -> Bool {
    (cp >= 0x09 && cp <= 0x0D) || cp == 0x20 || cp == 0x85 || cp == 0xA0 || cp == 0x1680
        || (cp >= 0x2000 && cp <= 0x200A) || cp == 0x2028 || cp == 0x2029 || cp == 0x202F
        || cp == 0x205F || cp == 0x3000
}

/// NFKD, then runs of the explicit whitespace set -> one U+0020, trimmed. Case is untouched.
public func normalizePassphrase(_ input: String) -> String {
    var out = String.UnicodeScalarView()
    var pendingSpace = false
    for scalar in input.decomposedStringWithCompatibilityMapping.unicodeScalars {
        if isProtocolWhitespace(scalar.value) {
            pendingSpace = !out.isEmpty
            continue
        }
        if pendingSpace { out.append(" "); pendingSpace = false }
        out.append(scalar)
    }
    return String(out)
}

public struct Keys: Sendable {
    public let passNorm: [UInt8]
    public let master: [UInt8]
    public let encKey: [UInt8]
    public let authToken: [UInt8]
    public let vaultId: String
}

func hexString(_ bytes: some Sequence<UInt8>) -> String {
    bytes.map { String(format: "%02x", $0) }.joined()
}

public func hexEncode(_ bytes: some Sequence<UInt8>) -> String { hexString(bytes) }

public func hexDecode(_ s: String) -> [UInt8]? {
    let chars = Array(s.utf8)
    guard chars.count % 2 == 0 else { return nil }
    func nib(_ c: UInt8) -> UInt8? {
        switch c {
        case 48...57: return c - 48
        case 97...102: return c - 87
        case 65...70: return c - 55
        default: return nil
        }
    }
    var out = [UInt8]()
    out.reserveCapacity(chars.count / 2)
    var i = 0
    while i < chars.count {
        guard let a = nib(chars[i]), let b = nib(chars[i + 1]) else { return nil }
        out.append(a << 4 | b)
        i += 2
    }
    return out
}

func pbkdf2SHA256(password: [UInt8], salt: [UInt8], iterations: Int, length: Int) throws -> [UInt8] {
    var derived = [UInt8](repeating: 0, count: length)
    let status = password.withUnsafeBufferPointer { pw in
        pw.withMemoryRebound(to: CChar.self) { pwc in
            CCKeyDerivationPBKDF(
                CCPBKDFAlgorithm(kCCPBKDF2), pwc.baseAddress, pw.count, salt, salt.count,
                CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), UInt32(iterations), &derived, length)
        }
    }
    guard status == kCCSuccess else { throw ProtocolError.keyDerivationFailed }
    return derived
}

public func deriveKeys(passphrase: String) throws -> Keys {
    let passNorm = Array(normalizePassphrase(passphrase).utf8)
    let master = try pbkdf2SHA256(password: passNorm, salt: Array("clipsync/v1/salt".utf8), iterations: 600_000, length: 32)
    let ikm = SymmetricKey(data: master)
    func hkdf(_ info: String) -> [UInt8] {
        // Empty salt: CryptoKit uses HashLen zeros, as RFC 5869 specifies.
        let k = HKDF<SHA256>.deriveKey(inputKeyMaterial: ikm, info: Data(info.utf8), outputByteCount: 32)
        return k.withUnsafeBytes { Array($0) }
    }
    let enc = hkdf("clipsync/v1/enc")
    let auth = hkdf("clipsync/v1/auth")
    return Keys(passNorm: passNorm, master: master, encKey: enc, authToken: auth,
                vaultId: hexString(SHA256.hash(data: auth)))
}

// MARK: - 3. identifiers (RFC 4122 order, independent of any platform UUID byte layout)

public func parseUUID(_ s: String) throws -> [UInt8] {
    let parts = s.split(separator: "-", omittingEmptySubsequences: false)
    guard parts.map(\.count) == [8, 4, 4, 4, 12], let bytes = hexDecode(parts.joined()), bytes.count == 16 else {
        throw ProtocolError.invalidUUID(s)
    }
    return bytes
}

public func formatUUID(_ b: [UInt8]) throws -> String {
    guard b.count == 16 else { throw ProtocolError.invalidLength("uuid must be 16 bytes") }
    let h = hexString(b)
    let c = Array(h)
    func slice(_ a: Int, _ z: Int) -> String { String(c[a..<z]) }
    return "\(slice(0, 8))-\(slice(8, 12))-\(slice(12, 16))-\(slice(16, 20))-\(slice(20, 32))"
}

public func uuidHex(_ b: [UInt8]) -> String { hexString(b) }

// MARK: - 4. encryption

private func u64be(_ n: UInt64) -> [UInt8] { (0..<8).map { UInt8(truncatingIfNeeded: n >> UInt64(56 - 8 * $0)) } }

public enum Part: UInt8, Sendable { case header = 1, body = 2, config = 3 }

public func itemAad(itemId: [UInt8], deviceId: [UInt8], createdAtMs: UInt64, part: Part) throws -> [UInt8] {
    guard itemId.count == 16, deviceId.count == 16, part != .config else { throw ProtocolError.invalidLength("ids must be 16 bytes") }
    return [1] + itemId + deviceId + u64be(createdAtMs) + [part.rawValue]
}

public func configAad(configVersion: UInt64) -> [UInt8] {
    [1] + u64be(configVersion) + [Part.config.rawValue]
}

/// Test-only entry point (`package` access, not public): fixed nonces must never reach production code.
package func sealWithNonce(key: [UInt8], nonce: [UInt8], aad: [UInt8], plaintext: [UInt8]) throws -> [UInt8] {
    guard nonce.count == 12 else { throw ProtocolError.invalidLength("nonce must be 12 bytes") }
    let box = try AES.GCM.seal(plaintext, using: SymmetricKey(data: key), nonce: AES.GCM.Nonce(data: nonce), authenticating: aad)
    return Array(box.nonce) + Array(box.ciphertext) + Array(box.tag)
}

public func seal(key: [UInt8], aad: [UInt8], plaintext: [UInt8]) throws -> [UInt8] {
    var nonce = [UInt8](repeating: 0, count: 12)
    for i in 0..<12 { nonce[i] = UInt8.random(in: 0...255) }   // SystemRandomNumberGenerator (CSPRNG)
    return try sealWithNonce(key: key, nonce: nonce, aad: aad, plaintext: plaintext)
}

public func open(key: [UInt8], aad: [UInt8], sealed: [UInt8]) throws -> [UInt8] {
    guard sealed.count >= 28 else { throw ProtocolError.tooShort }
    do {
        let box = try AES.GCM.SealedBox(combined: sealed)
        return Array(try AES.GCM.open(box, using: SymmetricKey(data: key), authenticating: aad))
    } catch {
        throw ProtocolError.decryptionFailed
    }
}

// MARK: - 5. preview

/// NFC first, then the first 200 Unicode scalars (code points, not Characters/graphemes).
public func previewOf(_ text: String) -> String {
    var out = String.UnicodeScalarView()
    out.append(contentsOf: text.precomposedStringWithCanonicalMapping.unicodeScalars.prefix(200))
    return String(out)
}

// MARK: - 6. bundle

public struct BundleEntry: Equatable, Sendable {
    public var type: UInt8
    public var name: String
    public var data: [UInt8]
    public init(type: UInt8, name: String, data: [UInt8]) {
        self.type = type; self.name = name; self.data = data
    }
}

private let bundleMagic: [UInt8] = Array("CSB1".utf8)

public func encodeBundle(_ entries: [BundleEntry]) throws -> [UInt8] {
    // Stable sort by type (file order preserved within a type).
    let sorted = entries.enumerated().sorted { ($0.element.type, $0.offset) < ($1.element.type, $1.offset) }.map(\.element)
    guard sorted.count <= 0xFFFF else { throw ProtocolError.bundle("too many entries") }
    var out = bundleMagic + [1, UInt8(sorted.count >> 8), UInt8(sorted.count & 0xFF)]
    for e in sorted {
        let name = Array(e.name.utf8)
        if (1...3).contains(e.type), !name.isEmpty { throw ProtocolError.bundle("type \(e.type) must have empty name") }
        guard name.count <= 0xFFFF, e.data.count <= Int(UInt32.max) else { throw ProtocolError.bundle("entry too large") }
        out.append(e.type)
        out += [UInt8(name.count >> 8), UInt8(name.count & 0xFF)]
        out += name
        let n = UInt32(e.data.count)
        out += [UInt8(n >> 24), UInt8((n >> 16) & 0xFF), UInt8((n >> 8) & 0xFF), UInt8(n & 0xFF)]
        out += e.data
    }
    return out
}

public func decodeBundle(_ b: [UInt8]) throws -> [BundleEntry] {
    guard b.count >= 7 else { throw ProtocolError.bundle("truncated (header)") }
    guard Array(b[0..<4]) == bundleMagic else { throw ProtocolError.bundle("bad magic") }
    guard b[4] == 1 else { throw ProtocolError.bundle("unsupported version \(b[4])") }
    let count = Int(b[5]) << 8 | Int(b[6])
    var off = 7
    var out = [BundleEntry]()
    for _ in 0..<count {
        guard off + 3 <= b.count else { throw ProtocolError.bundle("truncated (entry header)") }
        let type = b[off]
        let nameLen = Int(b[off + 1]) << 8 | Int(b[off + 2])
        off += 3
        guard off + nameLen + 4 <= b.count else { throw ProtocolError.bundle("truncated (name)") }
        guard let name = String(bytes: b[off..<off + nameLen], encoding: .utf8) else {
            throw ProtocolError.bundle("name is not UTF-8")
        }
        off += nameLen
        let dataLen = Int(b[off]) << 24 | Int(b[off + 1]) << 16 | Int(b[off + 2]) << 8 | Int(b[off + 3])
        off += 4
        guard off + dataLen <= b.count else { throw ProtocolError.bundle("truncated (data)") }
        let data = Array(b[off..<off + dataLen])
        off += dataLen
        if (1...3).contains(type), nameLen != 0 { throw ProtocolError.bundle("type \(type) must have empty name") }
        if (1...4).contains(type) { out.append(BundleEntry(type: type, name: name, data: data)) }   // unknown types skipped
    }
    guard off == b.count else { throw ProtocolError.bundle("trailing bytes after last entry") }
    return out
}
