using System.Text;
using System.Text.RegularExpressions;

namespace ClipSync.Core;

/// Windows `HTML Format`(CF_HTML). 오프셋은 **UTF-8 바이트** 기준 (S-3에서 검증한 스파이크 코드를 정리).
public static partial class CfHtml
{
    const string Pre = "<html>\r\n<body>\r\n<!--StartFragment-->";
    const string Post = "<!--EndFragment-->\r\n</body>\r\n</html>";
    const string Hdr = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";

    /// fragment를 감싼 CF_HTML 바이트(끝에 NUL). 헤더가 고정 폭이라 오프셋을 먼저 계산할 수 있다.
    public static byte[] Build(string fragment)
    {
        var hdrLen = Encoding.ASCII.GetByteCount(string.Format(Hdr, 0, 0, 0, 0));
        var startFrag = hdrLen + Encoding.UTF8.GetByteCount(Pre);
        var endFrag = startFrag + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFrag + Encoding.UTF8.GetByteCount(Post);
        return Encoding.UTF8.GetBytes(string.Format(Hdr, hdrLen, endHtml, startFrag, endFrag) + Pre + fragment + Post + "\0");
    }

    /// CF_HTML에서 fragment를 꺼낸다. 오프셋이 유효하지 않으면 `<!--StartFragment-->` 주석으로 폴백 (D-48).
    public static string? ExtractFragment(byte[] data)
    {
        var len = data.Length;
        while (len > 0 && data[len - 1] == 0) len--;
        if (len == 0) return null;
        var head = Encoding.ASCII.GetString(data, 0, Math.Min(len, 600));
        int Get(string key)
        {
            var m = Regex.Match(head, key + @":(-?\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : -1;
        }
        int sf = Get("StartFragment"), ef = Get("EndFragment");
        if (sf >= 0 && ef >= sf && ef <= len)
        {
            try { return new UTF8Encoding(false, true).GetString(data, sf, ef - sf); }
            catch (ArgumentException) { /* 오프셋이 문자 중간을 가리킴 → 폴백 */ }
        }
        var full = Encoding.UTF8.GetString(data, 0, len);
        var a = full.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        var b = full.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (a >= 0 && b > a) return full.Substring(a + 20, b - a - 20);
        var body = BodyContent().Match(full);
        return body.Success ? body.Groups[1].Value : null;
    }

    [GeneratedRegex(@"<body\b[^>]*>(.*)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex BodyContent();
}
