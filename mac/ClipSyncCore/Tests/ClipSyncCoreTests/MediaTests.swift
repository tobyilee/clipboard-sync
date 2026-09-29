import AppKit
import Foundation
import Testing
@testable import ClipSyncCore

@Suite struct MediaTests {
    let key = [UInt8](repeating: 7, count: 32)

    @Test func classifyFollowsSpec45() {
        let off = VaultConfig.failClosed
        let on = VaultConfig(images: true, files: true)
        // Finder 모양(파일 URL + 파일명 문자열 + 아이콘 TIFF): 파일 off면 조용히 무시 — 파일명 텍스트로 새지 않는다
        #expect(classify(hasFiles: true, hasImage: true, hasText: true, config: off) == .ignore)
        #expect(classify(hasFiles: true, hasImage: true, hasText: true, config: on) == .files)
        // 이미지 off + 텍스트 있음 → 텍스트만, 이미지만 있으면 무시
        #expect(classify(hasFiles: false, hasImage: true, hasText: true, config: off) == .text)
        #expect(classify(hasFiles: false, hasImage: true, hasText: false, config: off) == .ignore)
        #expect(classify(hasFiles: false, hasImage: true, hasText: true, config: on) == .image(withText: true))
        #expect(classify(hasFiles: false, hasImage: true, hasText: false, config: on) == .image(withText: false))
        #expect(classify(hasFiles: false, hasImage: false, hasText: true, config: off) == .text)
        #expect(classify(hasFiles: false, hasImage: false, hasText: false, config: on) == .ignore)
    }

    @Test func allowedKindsMapping() {
        #expect(VaultConfig.failClosed.allowedKinds == ["text", "html"])
        #expect(VaultConfig(images: true).allowedKinds == ["text", "html", "image"])
        #expect(VaultConfig(files: true).allowedKinds == ["text", "html", "files"])
    }

    @Test func sizeLimits() {
        #expect(plainSizeLimit(hasMedia: false, config: VaultConfig(images: true, maxMediaBytes: 50 << 20)) == 1 << 20)
        #expect(plainSizeLimit(hasMedia: true, config: VaultConfig(images: true, maxMediaBytes: 5 << 20)) == 5 << 20)
    }

    @Test func configJsonUsesSpecKeysAndClampsSize() throws {
        let json = String(decoding: try JSONEncoder().encode(VaultConfig(images: true, maxMediaBytes: 10 << 20)), as: UTF8.self)
        #expect(json.contains("\"max_media_bytes\":10485760"))
        let odd = try JSONDecoder().decode(VaultConfig.self, from: Data(#"{"v":1,"images":true,"files":false,"max_media_bytes":12345}"#.utf8))
        #expect(odd.maxMediaBytes == VaultConfig.defaultMediaBytes)
        #expect(odd.images)
    }

    @Test func configSealedAtVersionOpensOnlyAtThatVersion() throws {
        let cfg = VaultConfig(images: true, files: false, maxMediaBytes: 50 << 20)
        let sealed = try sealConfig(cfg, key: key, version: 4)
        #expect(try openConfig(ConfigBlob(version: 4, blob: sealed), key: key) == cfg)
        #expect(throws: (any Error).self) { try openConfig(ConfigBlob(version: 5, blob: sealed), key: key) }   // 옛 blob을 새 version으로 바꿔 끼우기 거부
    }

    static func samplePNG(width: Int = 40, height: Int = 30) -> Data {
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height, bitsPerSample: 8, samplesPerPixel: 4,
                                   hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        for y in 0..<height { for x in 0..<width {
            rep.setColor(NSColor(deviceRed: CGFloat(x) / CGFloat(width), green: CGFloat(y) / CGFloat(height), blue: 0.5,
                                 alpha: CGFloat(x + 1) / CGFloat(width)), atX: x, y: y)
        } }
        return rep.representation(using: .png, properties: [:])!
    }

    @Test func pngSizeFromIHDR() {
        let png = [UInt8](Self.samplePNG(width: 40, height: 30))
        #expect(pngSize(png)! == (40, 30))
        #expect(pngSize([1, 2, 3]) == nil)
    }

    @Test func pixelHashIsStableAcrossPngAndTiff() throws {
        let png = Self.samplePNG()
        let tiff = try #require(tiffData(fromPNG: png))
        let back = try #require(pngData(fromImageData: tiff))
        let h = try #require(imagePixelHash(png))
        #expect(imagePixelHash(tiff) == h)
        #expect(imagePixelHash(back) == h)
        #expect(imagePixelHash(Self.samplePNG(width: 41, height: 30)) != h)
        #expect(imagePixelHash(Data([1, 2, 3])) == nil)
    }
}
