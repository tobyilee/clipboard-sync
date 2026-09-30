import AppKit
import ClipSyncCore

/// NSPasteboard 읽기/쓰기 (spec 4.3, 4.5, 6.4, 7). 종류 판별은 `types`로 하고, 꺼진 타입은 내용을 읽지 않는다.
@MainActor
enum PasteboardIO {
    /// 에코 방지 1차 마커 (D-11): 원격 항목을 쓸 때 item_id를 함께 기록하고, 감시자는 이 타입이 있는 변경을 무시한다.
    static let markerType = NSPasteboard.PasteboardType("com.tobylee.clipsync.item")
    static let htmlType = NSPasteboard.PasteboardType.html

    struct Content: Sendable {
        var plain: String?
        var html: String?
    }

    /// 내용을 읽지 않고 표현 존재 여부만 본다 (spec 4.5 판별 입력).
    struct Shape {
        var own: Bool
        var hasFiles: Bool
        var hasImage: Bool
        var hasText: Bool
    }

    static func shape(_ pb: NSPasteboard = .general) -> Shape {
        let t = Set(pb.types ?? [])
        return Shape(own: t.contains(markerType), hasFiles: t.contains(.fileURL),
                     hasImage: t.contains(.png) || t.contains(.tiff),
                     hasText: t.contains(.string) || t.contains(htmlType) || t.contains(.rtf))
    }

    /// 텍스트/HTML 표현 (HTML → 없으면 RTF 변환, plain text fallback 포함).
    static func readText(_ pb: NSPasteboard = .general) -> Content? {
        let types = pb.types ?? []
        var plain = types.contains(.string) ? pb.string(forType: .string) : nil
        var html: String?
        if types.contains(htmlType), let d = pb.data(forType: htmlType) {
            html = String(data: d, encoding: .utf8) ?? pb.string(forType: htmlType)
        } else if types.contains(.rtf), let d = pb.data(forType: .rtf), let r = richText(fromRTF: d) {
            html = r.html   // RTF만 있는 소스 (TextEdit/Notes/Pages/Word)
            if plain == nil { plain = r.plain }
        }
        // plain text fallback은 가능하면 항상 포함한다 (4.3). 또 D-41 해시가 RDP를 통과하는 plain text 기준이 되도록 한다.
        if plain == nil, let h = html { plain = plainText(fromHTML: h) }
        return plain == nil && html == nil ? nil : Content(plain: plain, html: html)
    }

    /// 이미지 원본 바이트: PNG 우선, 없으면 TIFF (변환은 호출자가 메인 스레드 밖에서).
    static func readImage(_ pb: NSPasteboard = .general) -> (data: Data, isPNG: Bool)? {
        if let d = pb.data(forType: .png) { return (d, true) }
        if let d = pb.data(forType: .tiff) { return (d, false) }
        return nil
    }

    /// Finder 등이 복사한 파일 URL들.
    static func readFileURLs(_ pb: NSPasteboard = .general) -> [URL] {
        (pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL]) ?? []
    }

    /// 원격 파일 항목: 캐시에 다 쓴 파일들의 URL을 파일 URL 항목들로 기록한다 (첫 항목에 마커, D-56).
    @discardableResult
    static func writeFiles(_ urls: [URL], itemId: String, to pb: NSPasteboard = .general) -> Int {
        let items = urls.enumerated().map { i, url -> NSPasteboardItem in
            let item = NSPasteboardItem()
            item.setString(url.absoluteString, forType: .fileURL)
            if i == 0 { item.setData(Data(hexDecode(itemId) ?? []), forType: markerType) }
            return item
        }
        pb.clearContents()
        pb.writeObjects(items)
        return pb.changeCount
    }

    /// 원격 항목을 클립보드에 적용한다. `tiff`는 PNG를 못 읽는 앱을 위한 추가 표현(호출자가 미리 변환).
    /// 반환: 쓴 뒤의 changeCount (감시자가 자기 쓰기를 건너뛰는 데 쓴다).
    @discardableResult
    static func write(entries: [BundleEntry], tiff: Data? = nil, itemId: String, to pb: NSPasteboard = .general) -> Int {
        let item = NSPasteboardItem()
        for e in entries {
            switch e.type {
            case 1: item.setString(String(decoding: e.data, as: UTF8.self), forType: .string)
            case 2: item.setData(Data(htmlForApply(String(decoding: e.data, as: UTF8.self)).utf8), forType: htmlType)
            case 3: item.setData(Data(e.data), forType: .png)
            default: break   // 파일은 M5b
            }
        }
        if let tiff { item.setData(tiff, forType: .tiff) }
        item.setData(Data(hexDecode(itemId) ?? []), forType: markerType)
        pb.clearContents()
        pb.writeObjects([item])
        return pb.changeCount
    }

    /// D-49용: 현재 pasteboard 텍스트의 해시 (마커 유무와 관계없이). plain text 우선.
    static func currentTextHash(_ pb: NSPasteboard = .general) -> String? {
        let types = pb.types ?? []
        var plain = types.contains(.string) ? pb.string(forType: .string) : nil
        var html: String?
        if plain == nil, types.contains(htmlType), let d = pb.data(forType: htmlType) {
            html = String(data: d, encoding: .utf8)
            plain = html.flatMap { plainText(fromHTML: $0) }
        }
        return contentHash(plainText: plain, html: html)
    }

    /// macOS 15.4+ pasteboard 프라이버시: `.ask`/`.alwaysDeny`일 때만 권한 안내가 필요하다 (D-29).
    static var accessNeedsAttention: Bool {
        if #available(macOS 15.4, *) {
            switch NSPasteboard.general.accessBehavior {
            case .ask, .alwaysDeny: return true
            default: return false
            }
        }
        return false
    }
}
