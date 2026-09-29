using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

// S-3 spike: clipboard listener + dump of every format + synthetic writers + self-test.
class MainForm : Form
{
    const int WM_CLIPBOARDUPDATE = 0x031D;
    const string RichFragment = "<b>안녕하세요</b> <i>Hello</i> 😀 <span style=\"color:#c00\">red</span> <a href=\"https://example.com/?q=한글\">link</a>";
    const string RichText = "안녕하세요 Hello 😀 red link\r\nline2";

    readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
    readonly TextBox label = new() { Text = "manual", Width = 160 };
    readonly CheckBox auto = new() { Text = "auto-dump on update", Checked = true, AutoSize = true };
    readonly System.Windows.Forms.Timer debounce = new() { Interval = 150 };
    readonly string outRoot;
    readonly Stopwatch clock = Stopwatch.StartNew();
    long lastEventMs = -1, ownUntilMs = -1;
    int burst, burstOwn;
    byte[]? lastMarker;

    public MainForm()
    {
        Text = "S-3 clipboard spike";
        Width = 1100; Height = 760;
        outRoot = FindOutRoot();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        Btn(top, "Write text+HTML", WriteRich);
        Btn(top, "Write image (PNG+DIBV5)", WriteImage);
        Btn(top, "Write files (HDROP)", WriteFiles);
        Btn(top, "Self-test all", SelfTest);
        Btn(top, "Dump now", () => DumpNow(label.Text, true));
        Btn(top, "Clear log", () => log.Clear());
        top.Controls.Add(new Label { Text = "label:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        top.Controls.Add(label);
        top.Controls.Add(auto);
        Controls.Add(log);
        Controls.Add(top);

        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            var allOwn = burstOwn == burst;
            Log($"-- burst of {burst} update(s) settled (own={burstOwn}) --");
            var n = burst; burst = 0; burstOwn = 0;
            if (auto.Checked && !allOwn) DumpNow(label.Text, true, n);
        };
        Log($"output root: {outRoot}");
        Log("Copy things from Notepad/Edge/Explorer/Paint/(Office if installed). Set the 'label' box first (e.g. edge-selection). Each copy is dumped to out/<ts>-<label>/.");
    }

    static string FindOutRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "S3.csproj"))) d = d.Parent;
        var root = Path.Combine(d?.FullName ?? Environment.CurrentDirectory, "out");
        Directory.CreateDirectory(root);
        return root;
    }

    void Btn(Control parent, string text, Action a)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => { try { a(); } catch (Exception e) { Log("ERROR: " + e); } };
        parent.Controls.Add(b);
    }

    void Log(string s)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {s}";
        log.AppendText(line + "\r\n");
        try { File.AppendAllText(Path.Combine(outRoot, "session.log"), line + "\n"); } catch { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Log(Native.AddClipboardFormatListener(Handle) ? "AddClipboardFormatListener OK" : "AddClipboardFormatListener FAILED");
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Native.RemoveClipboardFormatListener(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            var now = clock.ElapsedMilliseconds;
            var own = now <= ownUntilMs;
            Log($"UPDATE seq={Native.GetClipboardSequenceNumber()} owner={Clip.OwnerName()} +{(lastEventMs < 0 ? 0 : now - lastEventMs)}ms{(own ? " [own write]" : "")}");
            lastEventMs = now; burst++; if (own) burstOwn++;
            debounce.Stop(); debounce.Start();
        }
        base.WndProc(ref m);
    }

    void DumpNow(string lbl, bool save, int updates = 0)
    {
        var items = Clip.Capture(Handle, out var attempts);
        if (items == null) { Log($"CAPTURE FAILED: OpenClipboard failed after {attempts} attempts"); return; }
        var dir = Path.Combine(outRoot, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Regex(lbl)}");
        var sb = new StringBuilder();
        void L(string s) { Log(s); sb.AppendLine(s); }
        L($"=== CAPTURE label={lbl} seq={Native.GetClipboardSequenceNumber()} openAttempts={attempts} updatesInBurst={updates} ===");
        Analyzer.Analyze(items, dir, lastMarker, L);
        if (save) File.WriteAllText(Path.Combine(dir, "log.txt"), sb.ToString());
    }

    static string Regex(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"[^\w\-]", "_");

    byte[] NewMarker() { lastMarker = RandomNumberGenerator.GetBytes(16); return lastMarker; }
    void MarkOwn() => ownUntilMs = clock.ElapsedMilliseconds + 1500;

    void WriteRich()
    {
        MarkOwn();
        Clip.Write(Handle, NewMarker(), new (uint, byte[])[]
        {
            (Clip.CF_UNICODETEXT, Clip.Utf16Z(RichText)),
            (Clip.FmtHtml, CfHtml.Build(RichFragment)),
        });
        Log("wrote text+HTML (paste into Notepad, Word/WordPad, Edge contenteditable)");
    }

    void WriteImage()
    {
        MarkOwn();
        using var bmp = Img.MakeTestBitmap();
        Clip.Write(Handle, NewMarker(), new (uint, byte[])[]
        {
            (Clip.FmtPng, Img.ToPng(bmp)),
            (Clip.CF_DIBV5, Img.BuildDibV5(bmp)),
        });
        Log("wrote PNG + CF_DIBV5 (paste into Paint, Word/WordPad; alpha should survive where supported)");
    }

    (string dir, string[] paths) MakeFiles()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardSync", "spike-s3", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        var files = new[] { "한글 파일.txt", "emoji😀.txt", "plain.txt" };
        foreach (var f in files) File.WriteAllText(Path.Combine(dir, f), "S-3 test " + f);
        var sub = Path.Combine(dir, "폴더");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "안쪽.txt"), "inside");
        return (dir, files.Select(f => Path.Combine(dir, f)).Append(sub).ToArray());
    }

    static byte[] BuildHDrop(string[] paths)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(20); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(1);   // DROPFILES: pFiles, pt.x, pt.y, fNC, fWide
        foreach (var p in paths) bw.Write(Encoding.Unicode.GetBytes(p + "\0"));
        bw.Write((short)0);
        bw.Flush();
        return ms.ToArray();
    }

    void WriteFiles()
    {
        MarkOwn();
        var (dir, paths) = MakeFiles();
        Clip.Write(Handle, NewMarker(), new (uint, byte[])[]
        {
            (Clip.CF_HDROP, BuildHDrop(paths)),
            (Clip.FmtDropEffect, BitConverter.GetBytes(1)),
        });
        Log($"wrote HDROP ({paths.Length} entries incl. one folder) from {dir} (paste into Explorer)");
    }

    void Check(string name, bool ok, string extra = "") => Log($"{(ok ? "PASS" : "FAIL")} {name} {extra}");

    void SelfTest()
    {
        Log("=== SELF-TEST ===");
        // rich
        WriteRich();
        var c = Clip.Capture(Handle, out _)!;
        var marker = c.FirstOrDefault(f => f.Id == Clip.FmtMarker)?.Data;
        Check("rich: marker reads back", marker != null && marker.AsSpan().SequenceEqual(lastMarker));
        Check("rich: CF_UNICODETEXT roundtrip", Analyzer.Decode16(c.First(f => f.Id == Clip.CF_UNICODETEXT).Data!) == RichText);
        var hf = c.FirstOrDefault(f => f.Id == Clip.FmtHtml);
        var p = hf?.Data == null ? null : CfHtml.Parse(hf.Data);
        Check("rich: HTML Format registered and present", hf != null, $"id={hf?.Id}");
        Check("rich: CF_HTML offsets valid (UTF-8 byte offsets)", p is { OffsetsOk: true });
        Check("rich: CF_HTML fragment roundtrip", p?.Fragment == RichFragment);
        var nc = c.FirstOrDefault(f => f.Id == Clip.FmtNoCloud)?.Data;
        Check("rich: CanUploadToCloudClipboard=0 readable", nc != null && BitConverter.ToInt32(nc, 0) == 0);

        // image
        WriteImage();
        using var orig = Img.MakeTestBitmap();
        var origPx = Img.GetBgra(orig, out _, out _);
        c = Clip.Capture(Handle, out _)!;
        var pngItem = c.FirstOrDefault(f => f.Id == Clip.FmtPng);
        var v5 = c.FirstOrDefault(f => f.Id == Clip.CF_DIBV5);
        var dib = c.FirstOrDefault(f => f.Id == Clip.CF_DIB);
        Check("image: registered 'PNG' present", pngItem != null && pngItem.Name == "PNG", $"id={pngItem?.Id}");
        Check("image: CF_DIBV5 (id 17) present", v5 != null);
        Log($"INFO image: synthesized CF_DIB present={dib != null}, CF_BITMAP present={c.Any(f => f.Id == Clip.CF_BITMAP)}");
        if (pngItem?.Data != null)
        {
            using var back = new Bitmap(new MemoryStream(pngItem.Data));
            Check("image: PNG pixel roundtrip (incl. alpha)", Img.Diff(origPx, Img.GetBgra(back, out _, out _)) == 0);
        }
        if (v5?.Data != null)
        {
            var r = Img.DibToPng(v5.Data);
            Check("image: DIBV5 -> pixels identical (alpha preserved)", r.bgra != null && Img.Diff(origPx, r.bgra) == 0, r.info);
        }
        if (dib?.Data != null)
        {
            var r = Img.DibToPng(dib.Data);
            Log($"INFO image: synthesized CF_DIB decode: {r.info} (pixel diff vs original: {(r.bgra == null ? "n/a" : Img.Diff(origPx, r.bgra).ToString())})");
        }

        // files
        WriteFiles();
        c = Clip.Capture(Handle, out _)!;
        var hd = c.FirstOrDefault(f => f.Id == Clip.CF_HDROP)?.Data;
        var parsed = hd == null ? null : Analyzer.ParseHDrop(hd);
        Check("files: HDROP parsed wide", parsed is { Wide: true });
        Check("files: 4 entries exist (3 files + 1 folder)", parsed != null && parsed.Paths.Count == 4 && parsed.Paths.All(x => File.Exists(x) || Directory.Exists(x)));
        Check("files: Korean + emoji names survive", parsed != null && parsed.Paths.Any(x => x.EndsWith("한글 파일.txt")) && parsed.Paths.Any(x => x.EndsWith("emoji😀.txt")));
        var de = c.FirstOrDefault(f => f.Id == Clip.FmtDropEffect)?.Data;
        Check("files: Preferred DropEffect=1", de != null && BitConverter.ToInt32(de, 0) == 1);
        Log("=== SELF-TEST DONE (now paste manually into the target apps and note results) ===");
    }
}
