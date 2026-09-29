import AppKit
import CryptoKit
import Foundation

/// TIFF 등 NSBitmapImageRep가 읽을 수 있는 이미지를 PNG로 (정규 포맷, spec 4.3).
public func pngData(fromImageData data: Data) -> Data? {
    NSBitmapImageRep(data: data)?.representation(using: .png, properties: [:])
}

/// PNG → TIFF (TIFF만 읽는 앱을 위해 적용 시 함께 기록).
public func tiffData(fromPNG png: Data) -> Data? {
    NSBitmapImageRep(data: png)?.tiffRepresentation
}

/// D-54: 디코드한 픽셀(sRGB, RGBA8 premultiplied)의 해시. 같은 이미지가 PNG/TIFF로 재인코딩돼도 같다 (같은 기기 안에서만 비교).
public func imagePixelHash(_ data: Data) -> String? {
    guard let rep = NSBitmapImageRep(data: data), let cg = rep.cgImage else { return nil }
    let w = cg.width, h = cg.height
    guard w > 0, h > 0, let space = CGColorSpace(name: CGColorSpace.sRGB),
          let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4, space: space,
                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
    ctx.draw(cg, in: CGRect(x: 0, y: 0, width: w, height: h))
    guard let px = ctx.data else { return nil }
    var hasher = SHA256()
    hasher.update(data: Data("img:\(w)x\(h):".utf8))
    hasher.update(bufferPointer: UnsafeRawBufferPointer(start: px, count: w * h * 4))
    return hexEncode(hasher.finalize())
}
