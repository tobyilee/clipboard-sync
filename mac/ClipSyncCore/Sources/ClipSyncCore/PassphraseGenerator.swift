import Foundation

/// 균등 분포 난수. `SecRandomCopyBytes` + 거절 샘플링으로 modulo 편향을 없앤다 (D-43).
public func secureRandomBelow(_ upperBound: Int, fill: (inout [UInt8]) throws -> Void = { try $0.withUnsafeMutableBytes { buf in
    guard SecRandomCopyBytes(kSecRandomDefault, buf.count, buf.baseAddress!) == errSecSuccess else { throw ProtocolError.randomFailure }
} }) throws -> Int {
    precondition(upperBound > 0 && upperBound <= 1 << 16)
    // 16비트 값에서 upperBound의 배수 미만인 값만 받아들인다.
    let limit = (65536 / upperBound) * upperBound
    while true {
        var b = [UInt8](repeating: 0, count: 2)
        try fill(&b)
        let v = Int(b[0]) << 8 | Int(b[1])
        if v < limit { return v % upperBound }
    }
}

/// EFF large wordlist 7단어(약 90비트)를 공백으로 이은 passphrase (spec 3.2, D-4, D-43).
public func generatePassphrase(wordCount: Int = 7) throws -> String {
    var words: [String] = []
    for _ in 0..<wordCount { words.append(effLargeWordlist[try secureRandomBelow(effLargeWordlist.count)]) }
    return words.joined(separator: " ")
}

/// 사용자가 직접 정한 passphrase는 정규화 후 24자 이상만 허용한다 (00-requirements, D-32).
public func isAcceptableCustomPassphrase(_ s: String) -> Bool {
    normalizePassphrase(s).count >= 24
}
