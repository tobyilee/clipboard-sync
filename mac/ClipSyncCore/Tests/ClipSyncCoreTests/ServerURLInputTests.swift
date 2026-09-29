import Foundation
import Testing
@testable import ClipSyncCore

@Suite struct ServerURLInputTests {
    @Test func acceptsHttpsAndTrimsSlashes() {
        #expect(ServerURLInput.parse(" https://clipsync-dev.clipboardsync.workers.dev/ ")?.absoluteString == "https://clipsync-dev.clipboardsync.workers.dev")
        #expect(ServerURLInput.parse("https://a.example.com:8443")?.port == 8443)
    }

    @Test func httpOnlyForLocalhost() {
        #expect(ServerURLInput.parse("http://localhost:8787") != nil)
        #expect(ServerURLInput.parse("http://127.0.0.1:8787") != nil)
        #expect(ServerURLInput.parse("http://example.com") == nil)
    }

    @Test func rejectsPathsQueriesAndGarbage() {
        #expect(ServerURLInput.parse("https://a.example.com/v1") == nil)
        #expect(ServerURLInput.parse("https://a.example.com?x=1") == nil)
        #expect(ServerURLInput.parse("") == nil)
        #expect(ServerURLInput.parse("not a url") == nil)
        #expect(ServerURLInput.parse("ftp://a.example.com") == nil)
    }
}
