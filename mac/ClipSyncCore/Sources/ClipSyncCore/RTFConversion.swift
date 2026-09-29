import AppKit
import Foundation

/// RTF만 제공하는 소스(TextEdit/Notes/Pages/Word)를 HTML fragment로 변환한다 (spec 4.3, D-26, D-39).
/// `NSAttributedString`의 HTML 내보내기는 WebKit을 쓰므로 메인 스레드에서 호출한다.
@MainActor
public func htmlFragment(fromRTF rtf: Data) -> String? {
    guard let attr = try? NSAttributedString(data: rtf, options: [.documentType: NSAttributedString.DocumentType.rtf], documentAttributes: nil),
          attr.length > 0,
          let data = try? attr.data(
            from: NSRange(location: 0, length: attr.length),
            documentAttributes: [.documentType: NSAttributedString.DocumentType.html, .characterEncoding: String.Encoding.utf8.rawValue])
    else { return nil }
    return htmlFragment(fromDocument: String(decoding: data, as: UTF8.self))
}

/// RTF 소스에서 HTML fragment와 plain text를 함께 얻는다. plain text fallback은 항상 있어야 한다 (spec 4.3, D-41).
@MainActor
public func richText(fromRTF rtf: Data) -> (html: String, plain: String)? {
    guard let html = htmlFragment(fromRTF: rtf),
          let attr = try? NSAttributedString(data: rtf, options: [.documentType: NSAttributedString.DocumentType.rtf], documentAttributes: nil)
    else { return nil }
    return (html, attr.string)
}

/// HTML만 있는 소스의 plain text fallback.
@MainActor
public func plainText(fromHTML html: String) -> String? {
    guard let attr = try? NSAttributedString(
        data: Data(htmlForApply(html).utf8),
        options: [.documentType: NSAttributedString.DocumentType.html, .characterEncoding: String.Encoding.utf8.rawValue],
        documentAttributes: nil), attr.length > 0 else { return nil }
    return attr.string
}
