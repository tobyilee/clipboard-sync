import CryptoKit
import Foundation

// 수신 파일명 정리 (spec D-57). protocol/ref/src/protocol.ts sanitizeFileName 과 같은 규칙이며 test-vectors.json 으로 확인한다.

private let reservedStems: Set<String> = Set(["con", "prn", "aux", "nul"] + (1...9).flatMap { ["com\($0)", "lpt\($0)"] })
private let badScalars: Set<Unicode.Scalar> = ["/", "\\", "<", ">", ":", "\"", "|", "?", "*"]
private let maxUTF16 = 150
private let maxUTF8 = 240

/// 끝의 '.'과 U+0020을 반복 제거
private func stripTrailing(_ s: String) -> String {
    var t = Substring(s)
    while let last = t.last, last == "." || last == " " { t.removeLast() }
    return String(t)
}

private func fits(_ s: String) -> Bool { s.utf16.count <= maxUTF16 && s.utf8.count <= maxUTF8 }

public func sanitizeFileName(_ name: String) -> String {
    var scalars = String.UnicodeScalarView()
    for u in name.precomposedStringWithCanonicalMapping.unicodeScalars {
        scalars.append(badScalars.contains(u) || u.value < 0x20 || u.value == 0x7F ? "_" : u)
    }
    var s = String(scalars)
    while s.hasPrefix(" ") { s.removeFirst() }
    while s.hasSuffix(" ") { s.removeLast() }
    s = stripTrailing(s)
    if s.isEmpty { return "file" }
    var stem = s.firstIndex(of: ".").map { String(s[..<$0]) } ?? s
    while stem.hasSuffix(" ") { stem.removeLast() }
    if reservedStems.contains(stem.lowercased()) { s = "_" + s }
    if fits(s) { return s }
    var ext = ""
    var baseScalars = Array(s.unicodeScalars)
    if let dot = s.lastIndex(of: "."), dot > s.startIndex {
        ext = String(s[dot...])
        if ext.utf16.count > 20 { ext = "" } else { baseScalars = Array(s[..<dot].unicodeScalars) }
    }
    while !baseScalars.isEmpty {
        var v = String.UnicodeScalarView(); v.append(contentsOf: baseScalars)
        if fits(String(v) + ext) { break }
        baseScalars.removeLast()
    }
    var v = String.UnicodeScalarView(); v.append(contentsOf: baseScalars)
    let base = stripTrailing(String(v))
    return (base.isEmpty ? "file" : base) + ext
}

/// 한 항목 안의 이름들을 정리하고, 대소문자 무시 중복이면 `이름 (2).ext`처럼 번호를 붙인다.
public func uniqueFileNames(_ names: [String]) -> [String] {
    var used = Set<String>()
    return names.map { n in
        let s = sanitizeFileName(n)
        var base = s, ext = ""
        if let dot = s.lastIndex(of: "."), dot > s.startIndex { base = String(s[..<dot]); ext = String(s[dot...]) }
        var out = s
        var i = 2
        while used.contains(out.lowercased()) { out = "\(base) (\(i))\(ext)"; i += 1 }
        used.insert(out.lowercased())
        return out
    }
}

/// D-54: 파일 항목의 에코 방지 해시 = (NFC 이름, 크기, 내용 SHA-256) 목록(이름순)의 해시.
public func filesHash(_ files: [(name: String, data: [UInt8])]) -> String {
    let lines = files.map { "\($0.name.precomposedStringWithCanonicalMapping)\t\($0.data.count)\t\(hexEncode(SHA256.hash(data: $0.data)))" }.sorted()
    return "files:" + hexEncode(SHA256.hash(data: Data(lines.joined(separator: "\n").utf8)))
}
