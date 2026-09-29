import Foundation
import Testing
@testable import ClipSyncCore

@Suite struct PassphraseTests {
    @Test func wordlistIsIntactAndTestVectorWordsPresent() {
        #expect(effLargeWordlist.count == 7776)
        #expect(Set(effLargeWordlist).count == 7776)
        #expect(effLargeWordlist.first == "abacus")
        #expect(effLargeWordlist.last == "zoom")
        for w in "abacus abdomen abide abnormal abrasion abroad absence".split(separator: " ") {
            #expect(effLargeWordlist.contains(String(w)))
        }
    }

    @Test func generatedPassphraseHasSevenListedWordsAndDerivesKeys() throws {
        let p = try generatePassphrase()
        let words = p.split(separator: " ").map(String.init)
        #expect(words.count == 7)
        #expect(words.allSatisfy { effLargeWordlist.contains($0) })
        #expect(p != (try generatePassphrase()))
        #expect(isAcceptableCustomPassphrase(p))
        _ = try deriveKeys(passphrase: p)
    }

    @Test func rejectionSamplingHasNoModuloBias() throws {
        // 상한 3, 16비트: 65536 = 3*21845+1 이므로 값 65535만 거절되어야 한다.
        var calls = 0
        let fill: (inout [UInt8]) throws -> Void = { b in
            calls += 1
            if calls == 1 { b = [0xFF, 0xFF] } else { b = [0x00, 0x05] }   // 거절 후 5 % 3 = 2
        }
        #expect(try secureRandomBelow(3, fill: fill) == 2)
        #expect(calls == 2)
    }

    @Test func distributionIsRoughlyUniform() throws {
        var counts = [Int](repeating: 0, count: 6)
        for _ in 0..<60000 { counts[try secureRandomBelow(6)] += 1 }
        for c in counts { #expect(abs(c - 10000) < 600) }   // ~6σ 여유
    }

    @Test func customPassphraseRequires24Chars() {
        #expect(!isAcceptableCustomPassphrase("short one"))
        #expect(isAcceptableCustomPassphrase("this passphrase is long enough"))
        #expect(!isAcceptableCustomPassphrase("   a   b   c   "))   // 공백 정규화 후 길이
    }
}
