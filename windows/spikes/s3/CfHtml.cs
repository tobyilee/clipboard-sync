using System.Text;
using System.Text.RegularExpressions;

static class CfHtml
{
    public record Parsed(string Full, string Fragment, int StartHtml, int EndHtml, int StartFragment, int EndFragment, string? SourceUrl, bool OffsetsOk);

    // Offsets are UTF-8 BYTE offsets. Header is fixed-width so offsets can be computed up front.
    public static byte[] Build(string fragment)
    {
        const string hdr = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string pre = "<html>\r\n<body>\r\n<!--StartFragment-->";
        const string post = "<!--EndFragment-->\r\n</body>\r\n</html>";
        var hdrLen = Encoding.ASCII.GetByteCount(string.Format(hdr, 0, 0, 0, 0));
        var startFrag = hdrLen + Encoding.UTF8.GetByteCount(pre);
        var endFrag = startFrag + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFrag + Encoding.UTF8.GetByteCount(post);
        var s = string.Format(hdr, hdrLen, endHtml, startFrag, endFrag) + pre + fragment + post;
        return Encoding.UTF8.GetBytes(s + "\0");
    }

    public static Parsed? Parse(byte[] data)
    {
        var len = data.Length;
        while (len > 0 && data[len - 1] == 0) len--;
        if (len == 0) return null;
        var head = Encoding.ASCII.GetString(data, 0, Math.Min(len, 600));
        int Get(string key)
        {
            var m = Regex.Match(head, key + @":(-?\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }
        int sh = Get("StartHTML"), eh = Get("EndHTML"), sf = Get("StartFragment"), ef = Get("EndFragment");
        var url = Regex.Match(head, @"SourceURL:(\S+)");
        var full = Encoding.UTF8.GetString(data, 0, len);
        var ok = sh >= 0 && eh >= sh && eh <= len && sf >= sh && ef >= sf && ef <= len;
        string frag;
        if (ok) frag = Encoding.UTF8.GetString(data, sf, ef - sf);
        else
        {
            var a = full.IndexOf("<!--StartFragment-->", StringComparison.Ordinal);
            var b = full.IndexOf("<!--EndFragment-->", StringComparison.Ordinal);
            frag = a >= 0 && b > a ? full.Substring(a + 20, b - a - 20) : "";
        }
        return new Parsed(full, frag, sh, eh, sf, ef, url.Success ? url.Groups[1].Value : null, ok);
    }
}
