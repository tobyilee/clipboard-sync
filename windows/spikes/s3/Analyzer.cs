using System.Text;
using System.Text.RegularExpressions;

static class Analyzer
{
    public static string Kind(FormatItem f) => f.Id switch { Clip.CF_UNICODETEXT => "text", Clip.CF_HDROP => "files", _ => f.Name };

    static FormatItem? Find(List<FormatItem> l, uint id) => l.FirstOrDefault(f => f.Id == id);

    public static string Decode16(byte[] d)
    {
        var s = Encoding.Unicode.GetString(d);
        var z = s.IndexOf('\0');
        return z >= 0 ? s[..z] : s;
    }

    public record HDrop(List<string> Paths, bool Wide);

    public static HDrop? ParseHDrop(byte[] d)
    {
        if (d.Length < 20) return null;
        var pFiles = BitConverter.ToInt32(d, 0);
        var wide = BitConverter.ToInt32(d, 16) != 0;
        if (pFiles < 20 || pFiles > d.Length) return null;
        var s = wide ? Encoding.Unicode.GetString(d, pFiles, d.Length - pFiles) : Encoding.Latin1.GetString(d, pFiles, d.Length - pFiles);
        var paths = new List<string>();
        foreach (var p in s.Split('\0')) { if (p.Length == 0) break; paths.Add(p); }
        return new HDrop(paths, wide);
    }

    // Logs every format, saves raw bytes + derived artifacts into dir, and reports the spec 4.5 classification.
    public static void Analyze(List<FormatItem> items, string dir, byte[]? myMarker, Action<string> log)
    {
        Directory.CreateDirectory(dir);
        log($"formats ({items.Count}):");
        var idx = 0;
        foreach (var f in items)
        {
            log($"  {f.Id,5} 0x{f.Id:X4} {f.Name,-42} {f.Size,10} B {f.Note}");
            if (f.Data != null)
                File.WriteAllBytes(Path.Combine(dir, $"{idx:D2}-{f.Id}-{Regex.Replace(f.Name, @"[^\w\-.]", "_")}.bin"), f.Data);
            idx++;
        }

        var marker = items.FirstOrDefault(f => f.Id == Clip.FmtMarker);
        if (marker?.Data != null)
            log($"MARKER present: {Convert.ToHexString(marker.Data)} {(myMarker != null && marker.Data.AsSpan().SequenceEqual(myMarker) ? "== our last write (OWN)" : "(not our last write)")}");
        else log("MARKER absent (foreign copy)");
        var nc = Find(items, Clip.FmtNoCloud);
        if (nc?.Data != null) log($"CanUploadToCloudClipboard = {BitConverter.ToInt32(nc.Data, 0)}");

        var hdrop = Find(items, Clip.CF_HDROP);
        var png = Find(items, Clip.FmtPng);
        var v5 = Find(items, Clip.CF_DIBV5);
        var dib = Find(items, Clip.CF_DIB);
        var bmp = Find(items, Clip.CF_BITMAP);
        var html = Find(items, Clip.FmtHtml);
        var txt = Find(items, Clip.CF_UNICODETEXT);
        var hasImg = png != null || v5 != null || dib != null || bmp != null;
        var kind = hdrop != null ? "files" : hasImg ? "image" : (html != null || txt != null) ? "text" : "none";
        var also = new List<string>();
        if (txt != null) also.Add("text");
        if (html != null) also.Add("html");
        if (hasImg && kind != "image") also.Add("image");
        if (hdrop != null && kind != "files") also.Add("files");
        log($"CLASSIFY (spec 4.5): {kind}; also present: {string.Join(",", also)}");
        if (kind == "image" && (txt != null || html != null)) log("NOTE: image classification WITH text/html present (Excel/Word-like) -> check whether spec 4.5 rule 2 is right for this source");
        if (kind == "files" && txt != null) log("NOTE: files + text present (Explorer-like); text must NOT be used as fallback (D-18)");

        if (txt?.Data != null)
        {
            var s = Decode16(txt.Data);
            File.WriteAllText(Path.Combine(dir, "derived.text.txt"), s);
            log($"TEXT: {s.Length} chars, CRLF={s.Contains("\r\n")} first80='{(s.Length > 80 ? s[..80] : s).Replace("\r", "\\r").Replace("\n", "\\n")}'");
        }
        if (html?.Data != null)
        {
            var p = CfHtml.Parse(html.Data);
            if (p == null) log("HTML: unparsable");
            else
            {
                File.WriteAllText(Path.Combine(dir, "derived.fragment.html"), p.Fragment);
                File.WriteAllText(Path.Combine(dir, "derived.full.html"), p.Full);
                log($"HTML: offsets StartHTML={p.StartHtml} EndHTML={p.EndHtml} StartFragment={p.StartFragment} EndFragment={p.EndFragment} bytes={html.Data.Length} offsetsOk={p.OffsetsOk} sourceUrl={p.SourceUrl}");
                log($"HTML: fragment {p.Fragment.Length} chars; fragment has class= {Regex.IsMatch(p.Fragment, @"\sclass\s*=")}; fragment has style= {Regex.IsMatch(p.Fragment, @"\sstyle\s*=")}; full has <style {p.Full.Contains("<style", StringComparison.OrdinalIgnoreCase)}");
            }
        }
        if (hasImg)
        {
            if (png?.Data != null)
            {
                File.WriteAllBytes(Path.Combine(dir, "derived.png"), png.Data);
                try { using var img = Image.FromStream(new MemoryStream(png.Data)); log($"IMAGE: PNG format {img.Width}x{img.Height}, {png.Data.Length} B"); }
                catch (Exception e) { log("IMAGE: PNG decode failed: " + e.Message); }
            }
            foreach (var (item, tag) in new[] { (v5, "dibv5"), (dib, "dib") })
            {
                if (item?.Data == null) continue;
                var r = Img.DibToPng(item.Data);
                log($"IMAGE: {tag} -> {(r.png != null ? "PNG " + r.png.Length + " B" : "FAILED")} :: {r.info}");
                if (r.png != null) File.WriteAllBytes(Path.Combine(dir, $"derived-from-{tag}.png"), r.png);
            }
            if (bmp != null && png == null && v5 == null && dib == null) log("IMAGE: only CF_BITMAP handle present (not readable here)");
        }
        var de = Find(items, Clip.FmtDropEffect);
        if (de?.Data != null) log($"DropEffect = {BitConverter.ToInt32(de.Data, 0)} (1=copy 2=move 4=link)");
        if (hdrop?.Data != null)
        {
            var hd = ParseHDrop(hdrop.Data);
            if (hd == null) log("HDROP: unparsable");
            else
            {
                log($"HDROP: {hd.Paths.Count} entries, wide={hd.Wide}");
                foreach (var p in hd.Paths)
                    log($"  {(Directory.Exists(p) ? "DIR " : File.Exists(p) ? "FILE" : "MISSING")} {p}{(File.Exists(p) ? " " + new FileInfo(p).Length + " B" : "")}");
            }
        }
    }
}
