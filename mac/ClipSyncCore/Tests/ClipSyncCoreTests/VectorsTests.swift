import Foundation
import Testing
@testable import ClipSyncCore

// Consumer-style tests mirroring protocol/ref/test/vectors.test.ts.
private func loadVectors(file: String = #filePath) throws -> [String: Any] {
    var dir = URL(fileURLWithPath: file).deletingLastPathComponent()
    for _ in 0..<12 {
        let candidate = dir.appendingPathComponent("protocol/test-vectors.json")
        if FileManager.default.fileExists(atPath: candidate.path) {
            return try JSONSerialization.jsonObject(with: Data(contentsOf: candidate)) as! [String: Any]
        }
        dir.deleteLastPathComponent()
    }
    throw ProtocolError.bundle("protocol/test-vectors.json not found above \(file)")
}

private func hx(_ s: Any?) -> [UInt8] { hexDecode(s as! String)! }
private func list(_ v: [String: Any], _ key: String) -> [[String: Any]] { v[key] as! [[String: Any]] }
private func entries(_ l: Any?) -> [BundleEntry] {
    (l as! [[String: Any]]).map { BundleEntry(type: UInt8(truncating: $0["type"] as! NSNumber), name: $0["name"] as! String, data: hx($0["data_hex"])) }
}
private func canonicalJSON(_ bytes: [UInt8]) throws -> NSObject {
    try JSONSerialization.jsonObject(with: Data(bytes), options: [.fragmentsAllowed]) as! NSObject
}

@Suite struct VectorTests {
    let v: [String: Any]
    init() throws { v = try loadVectors() }

    @Test func passphrases() throws {
        for p in list(v, "passphrases") {
            let name = p["name"] as! String
            let k = try deriveKeys(passphrase: p["passphrase"] as! String)
            #expect(hexEncode(k.passNorm) == p["pass_norm_hex"] as! String, "\(name) pass_norm")
            #expect(hexEncode(k.master) == p["master"] as! String, "\(name) master")
            #expect(hexEncode(k.encKey) == p["enc_key"] as! String, "\(name) enc_key")
            #expect(hexEncode(k.authToken) == p["auth_token"] as! String, "\(name) auth_token")
            #expect(k.vaultId == p["vault_id"] as! String, "\(name) vault_id")
        }
        let by = { (n: String) in list(v, "passphrases").first { $0["name"] as! String == n }! }
        #expect(by("ws-variant-of-ascii")["vault_id"] as! String == by("ascii")["vault_id"] as! String)
        #expect(by("case-differs")["vault_id"] as! String != by("ascii")["vault_id"] as! String)
        #expect(by("zwsp-feff-not-whitespace")["vault_id"] as! String != by("ascii")["vault_id"] as! String)
    }

    @Test func normalizeEdgeCases() {
        #expect(normalizePassphrase("   ") == "")
        #expect(normalizePassphrase("a\u{3000}\u{3000}b") == "a b")
        #expect(normalizePassphrase(" a  b ") == "a b")
        #expect(normalizePassphrase("a\u{200B}b") == "a\u{200B}b")
        #expect(normalizePassphrase("a\u{FEFF}b") == "a\u{FEFF}b")
        #expect(normalizePassphrase("\u{2028}a\u{2029}") == "a")
    }

    @Test func uuids() throws {
        for u in list(v, "uuids") {
            let b = try parseUUID(u["input"] as! String)
            #expect(hexEncode(b) == u["bytes_hex"] as! String)
            #expect(try formatUUID(b) == u["canonical"] as! String)
            #expect(uuidHex(b) == u["hex_id"] as! String)
        }
    }

    @Test func seals() throws {
        let keys = Dictionary(uniqueKeysWithValues: list(v, "passphrases").map { ($0["name"] as! String, hx($0["enc_key"])) })
        for s in list(v, "seals") {
            let name = s["name"] as! String
            let part = s["part"] as! String
            let aad: [UInt8]
            if part == "config" {
                aad = configAad(configVersion: UInt64(s["config_version"] as! String)!)
            } else {
                aad = try itemAad(itemId: parseUUID(s["item_id_uuid"] as! String), deviceId: parseUUID(s["device_id_uuid"] as! String),
                                  createdAtMs: UInt64(s["created_at"] as! String)!, part: part == "header" ? .header : .body)
            }
            #expect(hexEncode(aad) == s["aad_hex"] as! String, "\(name) aad")
            let key = keys[s["key_from"] as! String]!
            let sealed = try sealWithNonce(key: key, nonce: hx(s["nonce"]), aad: aad, plaintext: hx(s["plaintext_hex"]))
            #expect(hexEncode(sealed) == s["sealed_hex"] as! String, "\(name) sealed")
            let pt = try open(key: key, aad: aad, sealed: hx(s["sealed_hex"]))
            #expect(hexEncode(pt) == s["plaintext_hex"] as! String, "\(name) open")
            if let expected = s["expect_json"] {   // compare parsed fields, never encoder bytes
                #expect(try canonicalJSON(pt) == (expected as! NSObject), "\(name) json")
            }
        }
    }

    @Test func randomNonceRoundtrip() throws {
        let key = hx(list(v, "passphrases")[0]["enc_key"]), aad = hx(list(v, "seals")[0]["aad_hex"])
        let a = try seal(key: key, aad: aad, plaintext: [0x78]), b = try seal(key: key, aad: aad, plaintext: [0x78])
        #expect(a != b)
        #expect(try open(key: key, aad: aad, sealed: a) == [0x78])
    }

    @Test func negatives() {
        for n in list(v, "negatives") {
            #expect(n["expect"] as! String == "fail")
            #expect(throws: (any Error).self, "\(n["name"] as! String)") {
                _ = try open(key: hx(n["key_hex"]), aad: hx(n["aad_hex"]), sealed: hx(n["sealed_hex"]))
            }
        }
    }

    @Test func bundles() throws {
        for b in list(v, "bundles") {
            let name = b["name"] as! String
            let bytes = hx(b["bytes_hex"])
            #expect(try decodeBundle(bytes) == entries(b["entries"]), "\(name) decode")
            if (b["decode_only"] as? Bool) != true {
                #expect(try encodeBundle(entries(b["encode_input"] ?? b["entries"])) == bytes, "\(name) encode")
            }
        }
    }

    @Test func bundleInvalid() {
        for b in list(v, "bundle_invalid") {
            #expect(b["expect"] as! String == "error")
            #expect(throws: (any Error).self, "\(b["name"] as! String)") { _ = try decodeBundle(hx(b["bytes_hex"])) }
        }
    }

    @Test func fileNames() {
        for f in list(v, "file_names") {
            #expect(sanitizeFileName(f["input"] as! String) == f["expect"] as! String, "\(f["name"] as! String)")
        }
        for f in list(v, "file_name_sets") {
            #expect(uniqueFileNames(f["inputs"] as! [String]) == f["expect"] as! [String], "\(f["name"] as! String)")
        }
    }

    @Test func previews() {
        for p in list(v, "previews") {
            #expect(previewOf(p["input"] as! String) == p["output"] as! String, "\(p["name"] as! String)")
        }
    }
}
