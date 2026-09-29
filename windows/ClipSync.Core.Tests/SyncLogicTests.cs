using System.Text;
using ClipSync.Core;
using Xunit;

namespace ClipSync.Core.Tests;

// mac SyncLogicTests.swift 와 같은 규칙을 확인한다.
public class SyncLogicTests
{
    [Fact]
    public void HashNormalizationMakesEquivalentTextsEqual()
    {
        Assert.Equal(SyncLogic.ContentHash("line1\r\nline2\0\0", null), SyncLogic.ContentHash("line1\nline2", null));
        Assert.Equal(SyncLogic.ContentHash("é", null), SyncLogic.ContentHash("é", null));
        Assert.NotEqual(SyncLogic.ContentHash("a", null), SyncLogic.ContentHash("b", null));
        Assert.Null(SyncLogic.ContentHash(null, null));
    }

    [Fact]
    public void HashIsSha256OfNormalizedUtf8()
    {
        // 각 기기는 자기 해시만 비교하지만, 규칙은 Swift와 같게 둔다: SHA256(정규화 UTF-8) 소문자 hex.
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("한글\n"))),
                     SyncLogic.ContentHash("한글\r\n", null));
    }

    [Fact]
    public void PlainTextTakesPriorityOverHtml()
    {
        Assert.Equal(SyncLogic.ContentHash("hi", "<b>hi</b>"), SyncLogic.ContentHash("hi", "<i>hi</i>"));
        Assert.NotEqual(SyncLogic.ContentHash(null, "<b>hi</b>"), SyncLogic.ContentHash("hi", null));
    }

    [Fact]
    public void RingKeepsFiveEntriesForSixtySeconds()
    {
        var r = new RecentHashRing();
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1000);
        for (var i = 0; i < 6; i++) r.Add($"h{i}", t0);
        Assert.False(r.Contains("h0", t0));
        Assert.True(r.Contains("h5", t0.AddSeconds(59)));
        Assert.False(r.Contains("h5", t0.AddSeconds(61)));
    }

    [Fact]
    public void ToCrlfConvertsOnlyLoneLf()
    {
        Assert.Equal("a\r\nb\r\nc", SyncLogic.ToCrlf("a\nb\r\nc"));
        Assert.Equal("no newline", SyncLogic.ToCrlf("no newline"));
        Assert.Equal(SyncLogic.ContentHash("a\nb", null), SyncLogic.ContentHash(SyncLogic.ToCrlf("a\nb"), null)); // 적용 후 에코 dedupe 유지
    }

    [Fact]
    public void PlainFromHtmlStripsTagsStylesAndEntities()
    {
        Assert.Equal("한글 bold & more", SyncLogic.PlainFromHtml("<style>p{x:1}</style><p>한글 <b>bold</b> &amp; more</p>"));
        Assert.Equal("a\nb", SyncLogic.PlainFromHtml("a<br>b"));
        Assert.Null(SyncLogic.PlainFromHtml("<p> </p>"));
    }

    static CatchUpCandidate C(long seq, string dev = "peer", bool purged = false, params string[] kinds) =>
        new(seq, dev, purged, kinds.Length == 0 ? ["text"] : kinds);

    [Fact]
    public void CatchUpPicksHighestApplicableSeq()
    {
        var plan = SyncLogic.PlanCatchUp([C(5), C(6), C(7, "me"), C(8, purged: true)], 4, "me", true, new HashSet<string> { "text", "html" });
        Assert.Equal(8, plan.NewLastSeq);
        Assert.Equal(6, plan.Target?.Seq);
    }

    [Fact]
    public void CatchUpSkipsOldAndDisallowedKinds()
    {
        var plan = SyncLogic.PlanCatchUp([C(3), C(9, kinds: "image"), C(10, kinds: ["text", "files"])], 3, "me", true, new HashSet<string> { "text", "html" });
        Assert.Equal(10, plan.NewLastSeq);
        Assert.Null(plan.Target);
    }

    [Fact]
    public void CatchUpWhenReceiveOffOnlyAdvancesSeq()
    {
        Assert.Equal(new CatchUpPlan(5, null), SyncLogic.PlanCatchUp([C(5)], 0, "me", false, new HashSet<string> { "text" }));
        Assert.Equal(7, SyncLogic.PlanCatchUp([], 7, "me", true, new HashSet<string> { "text" }).NewLastSeq);
    }

    [Theory]
    [InlineData(" https://clipsync-dev.clipboardsync.workers.dev/ ", "https://clipsync-dev.clipboardsync.workers.dev/")]
    [InlineData("http://localhost:8787", "http://localhost:8787/")]
    [InlineData("http://127.0.0.1:8787", "http://127.0.0.1:8787/")]
    public void UrlAccepted(string input, string expected) => Assert.Equal(expected, ServerUrlInput.Parse(input)?.ToString());

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://a.example.com/v1")]
    [InlineData("https://a.example.com?x=1")]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://a.example.com")]
    public void UrlRejected(string input) => Assert.Null(ServerUrlInput.Parse(input));
}

public class CfHtmlTests
{
    [Fact]
    public void BuildHasByteOffsetsForKoreanAndEmoji()
    {
        var frag = "<p>한글 🎉 <b>bold</b></p>";
        var data = CfHtml.Build(frag);
        Assert.Equal(0, data[^1]);
        var text = Encoding.UTF8.GetString(data, 0, data.Length - 1);
        int Off(string key) => int.Parse(System.Text.RegularExpressions.Regex.Match(text, key + @":(\d+)").Groups[1].Value);
        Assert.Equal(frag, Encoding.UTF8.GetString(data, Off("StartFragment"), Off("EndFragment") - Off("StartFragment")));
        Assert.Equal("<html>", Encoding.UTF8.GetString(data, Off("StartHTML"), 6));
        Assert.Equal(data.Length - 1, Off("EndHTML"));
        Assert.Equal(frag, CfHtml.ExtractFragment(data));
    }

    [Fact]
    public void BrokenOffsetsFallBackToComments()
    {
        var s = "Version:0.9\r\nStartHTML:0000000999\r\nEndHTML:0000009999\r\nStartFragment:0000000005\r\nEndFragment:0000009999\r\n" +
                "<html><body><!--StartFragment--><p>한글</p><!--EndFragment--></body></html>";
        Assert.Equal("<p>한글</p>", CfHtml.ExtractFragment(Encoding.UTF8.GetBytes(s)));
    }

    [Fact]
    public void OffsetInsideMultibyteCharFallsBack()
    {
        var good = CfHtml.Build("<p>한</p>");
        var text = Encoding.UTF8.GetString(good);
        var sf = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"StartFragment:(\d+)").Groups[1].Value);
        var broken = text.Replace($"StartFragment:{sf:D10}", $"StartFragment:{sf + 4:D10}"); // '한'(3바이트) 중간
        Assert.Equal("<p>한</p>", CfHtml.ExtractFragment(Encoding.UTF8.GetBytes(broken)));
    }

    [Fact]
    public void NoMarkersUsesBodyOrNull()
    {
        Assert.Equal("<i>x</i>", CfHtml.ExtractFragment(Encoding.UTF8.GetBytes("<html><body><i>x</i></body></html>")));
        Assert.Null(CfHtml.ExtractFragment([0, 0]));
    }
}
