import AppKit
import ClipSyncCore

/// NSPasteboard 읽기/쓰기 (spec 4.3, 4.5, 6.4, 7). 종류 판별은 `types`로 하고, 꺼진 타입은 내용을 읽지 않는다.
@MainActor
enum PasteboardIO {
    /// 에코 방지 1차 마커 (D-11): 원격 항목을 쓸 때 item_id를 함께 기록하고, 감시자는 이 타입이 있는 변경을 무시한다.
    static let markerType = NSPasteboard.PasteboardType("com.tobylee.clipsync.item")
    static let htmlType = NSPasteboard.PasteboardType.html

    struct Content {
        var plain: String?
        var html: String?
    }

    enum ReadResult {
        case ownMarker          // 우리가 쓴 변경
        case ignored(String)    // 보낼 것이 아님 (사유는 디버그용)
        case content(Content)
    }

    static func read(_ pb: NSPasteboard = .general) -> ReadResult {
        let types = pb.types ?? []
        if types.contains(markerType) { return .ownMarker }
        // 4.5-1: 파일이 있으면 항목 전체 무시 (파일명 문자열로 폴백하지 않는다). 파일 동기화는 M5.
        if types.contains(.fileURL) { return .ignored("files") }

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
        if plain == nil && html == nil {
            let hasImage = types.contains(.png) || types.contains(.tiff)
            return .ignored(hasImage ? "image (off)" : "no text")
        }
        return .content(Content(plain: plain, html: html))
    }

    /// 원격 항목을 클립보드에 적용한다. 반환: 쓴 뒤의 changeCount (감시자가 자기 쓰기를 건너뛰는 데 쓴다).
    @discardableResult
    static func write(entries: [BundleEntry], itemId: String, to pb: NSPasteboard = .general) -> Int {
        let item = NSPasteboardItem()
        for e in entries {
            switch e.type {
            case 1: item.setString(String(decoding: e.data, as: UTF8.self), forType: .string)
            case 2: item.setData(Data(htmlForApply(String(decoding: e.data, as: UTF8.self)).utf8), forType: htmlType)
            default: break   // 이미지/파일은 M5
            }
        }
        item.setData(Data(hexDecode(itemId) ?? []), forType: markerType)
        pb.clearContents()
        pb.writeObjects([item])
        return pb.changeCount
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
