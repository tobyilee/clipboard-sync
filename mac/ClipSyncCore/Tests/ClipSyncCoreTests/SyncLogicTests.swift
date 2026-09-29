import Foundation
import Testing
@testable import ClipSyncCore

@Suite struct SyncLogicTests {
    @Test func hashNormalizationMakesEquivalentTextsEqual() {
        let nfd = "e\u{0301}"   // é (NFD)
        let a = contentHash(plainText: "line1\r\nline2\0\0", html: nil)
        let b = contentHash(plainText: "line1\nline2", html: nil)
        #expect(a == b)
        #expect(contentHash(plainText: nfd, html: nil) == contentHash(plainText: "é", html: nil))
        #expect(contentHash(plainText: "a", html: nil) != contentHash(plainText: "b", html: nil))
        #expect(contentHash(plainText: nil, html: nil) == nil)
    }

    @Test func plainTextTakesPriorityOverHtml() {
        // D-41: plain text가 있으면 HTML이 달라도 같은 해시 (RDP는 plain text만 통과시킨다)
        #expect(contentHash(plainText: "hi", html: "<b>hi</b>") == contentHash(plainText: "hi", html: "<i>hi</i>"))
        #expect(contentHash(plainText: nil, html: "<b>hi</b>") != contentHash(plainText: "hi", html: nil))
    }

    @Test func ringKeepsFiveEntriesForSixtySeconds() {
        var r = RecentHashRing()
        let t0 = Date(timeIntervalSince1970: 1000)
        for i in 0..<6 { r.add("h\(i)", now: t0) }
        let h0 = r.contains("h0", now: t0)
        let h5At59 = r.contains("h5", now: t0.addingTimeInterval(59))
        let h5At61 = r.contains("h5", now: t0.addingTimeInterval(61))
        #expect(!h0)         // 5개 초과분은 밀려남
        #expect(h5At59)
        #expect(!h5At61)     // 60초 후 만료
    }

    @Test func htmlFragmentKeepsStylesAndDropsDocumentShell() {
        let doc = """
        <!DOCTYPE html><html><head><meta charset="utf-8"><title></title>
        <style type="text/css">p.p1 {margin:0; font: 12px Helvetica}</style></head>
        <BODY class="x"><p class="p1">한글 <b>bold</b></p></BODY></html>
        """
        let f = htmlFragment(fromDocument: doc)
        #expect(f.hasPrefix("<style"))
        #expect(f.contains("p.p1 {margin:0"))
        #expect(f.contains("<p class=\"p1\">한글 <b>bold</b></p>"))
        #expect(!f.contains("<html") && !f.contains("<body") && !f.lowercased().contains("<title"))
        #expect(htmlFragment(fromDocument: "<b>x</b>") == "<b>x</b>")   // 껍데기가 없으면 그대로
    }

    @Test func htmlForApplyAddsCharsetOnlyWhenMissing() {
        #expect(htmlForApply("<p>한글🎉</p>") == "<meta charset='utf-8'><p>한글🎉</p>")
        #expect(htmlForApply("<meta charset=\"utf-8\"><p>x</p>") == "<meta charset=\"utf-8\"><p>x</p>")
    }

    @Test @MainActor func rtfConvertsToHtmlWithFormattingAndUnicode() throws {
        let rtf = #"{\rtf1\ansi{\fonttbl\f0\fswiss Helvetica;}\f0\fs24 hello \b bold\b0  \uc0\u54620 \u44544 }"#   // TextEdit 형식(양수 \u)
        let html = try #require(htmlFragment(fromRTF: Data(rtf.utf8)))
        #expect(html.contains("hello"))
        #expect(html.contains("한글"))
        #expect(html.lowercased().contains("<style"))
        #expect(!html.lowercased().contains("<body"))
        #expect(htmlFragment(fromRTF: Data()) == nil)
    }

    private func c(_ seq: Int, dev: String = "peer", purged: Bool = false, kinds: [String] = ["text"]) -> CatchUpCandidate {
        CatchUpCandidate(seq: seq, deviceId: dev, purged: purged, kinds: kinds)
    }

    @Test func catchUpPicksHighestApplicableSeq() {
        let plan = planCatchUp(items: [c(5), c(6), c(7, dev: "me"), c(8, purged: true)], lastSeq: 4, selfDevice: "me",
                               canApply: true, allowedKinds: ["text", "html"])
        #expect(plan.newLastSeq == 8)
        #expect(plan.target?.seq == 6)   // 자기 항목(7), purged(8)는 후보에서 제외
    }

    @Test func catchUpSkipsOldAndDisallowedKinds() {
        let plan = planCatchUp(items: [c(3), c(9, kinds: ["image"]), c(10, kinds: ["text", "files"])], lastSeq: 3, selfDevice: "me",
                               canApply: true, allowedKinds: ["text", "html"])
        #expect(plan.newLastSeq == 10)
        #expect(plan.target == nil)      // 이미지/파일 포함 항목은 config에서 꺼져 있으면 적용 안 함 (fail-closed)
    }

    @Test func catchUpWhenReceiveOffOnlyAdvancesSeq() {
        let plan = planCatchUp(items: [c(5)], lastSeq: 0, selfDevice: "me", canApply: false, allowedKinds: ["text"])
        #expect(plan == CatchUpPlan(newLastSeq: 5, target: nil))
        #expect(planCatchUp(items: [], lastSeq: 7, selfDevice: "me", canApply: true, allowedKinds: ["text"]).newLastSeq == 7)
    }

    @Test @MainActor func richTextProvidesPlainFallback() throws {
        let b = String(UnicodeScalar(92))
        let rtf = "{" + b + "rtf1" + b + "ansi " + b + "b bold" + b + "b0  text}"
        let r = try #require(richText(fromRTF: Data(rtf.utf8)))
        #expect(r.plain.contains("bold text"))
        #expect(r.html.contains("<b>bold</b>"))
        #expect(plainText(fromHTML: "<p>한글 <b>bold</b></p>")?.contains("한글 bold") == true)
        #expect(plainText(fromHTML: "") == nil)
    }
}
