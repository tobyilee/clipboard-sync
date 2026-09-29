using System.Text;
using ClipSync.Core;

namespace ClipSync.App;

/// `ClipSync.exe --selftest`: Cloud PC에서 스크린샷 한 장으로 Win32 계층을 확인한다 (클립보드 내용을 덮어쓴다).
/// 실제 passphrase 항목은 건드리지 않는다 (Credential 대상 `ClipSync/selftest`).
static class SelfTest
{
    public static int Run()
    {
        var lines = new List<string>();
        var failed = 0;
        void Check(string name, Func<string?> test)
        {
            string? detail;
            bool ok;
            try { detail = test(); ok = detail is null; }
            catch (Exception e) { detail = e.GetType().Name + ": " + e.Message; ok = false; }
            if (!ok) failed++;
            lines.Add($"{(ok ? "PASS" : "FAIL")}  {name}{(ok ? "" : " — " + detail)}");
        }

        Check("credential round-trip (ClipSync/selftest)", () =>
        {
            var saved = CredentialStore.Target;
            CredentialStore.Target = "ClipSync/selftest";
            try
            {
                CredentialStore.Save("selftest passphrase 한글");
                var back = CredentialStore.Load();
                CredentialStore.Delete();
                var gone = CredentialStore.Load();
                return back == "selftest passphrase 한글" && gone is null ? null : $"back={back is not null} gone={gone is null}";
            }
            finally { CredentialStore.Target = saved; }
        });

        using var listener = new ClipboardListener();
        var notified = 0;
        listener.Changed += () => notified++;
        var hwnd = listener.Handle;
        var id = ServerClient.NewDeviceId();   // 임의 16바이트 id
        const string text = "selftest 한글 🎉\nline2";
        const string frag = "<p>selftest <b>한글</b> 🎉</p>";
        uint writtenSeq = 0;

        Check("write text+html with marker", () =>
        {
            writtenSeq = ClipboardIO.Write(hwnd, [new BundleEntry(1, "", Encoding.UTF8.GetBytes(text)), new BundleEntry(2, "", Encoding.UTF8.GetBytes(frag))], id);
            return null;
        });
        Check("sequence number after write is stable", () =>
            ClipboardIO.SequenceNumber == writtenSeq ? null : $"written={writtenSeq} now={ClipboardIO.SequenceNumber}");
        Check("listener gets WM_CLIPBOARDUPDATE", () =>
        {
            var until = Environment.TickCount64 + 1000;
            while (notified == 0 && Environment.TickCount64 < until) { Application.DoEvents(); Thread.Sleep(10); }
            return notified > 0 ? null : "no message within 1s";
        });
        Check("shape sees own marker", () => ClipboardIO.HasOwnMarker(hwnd) ? null : "marker not detected");
        Check("text applied as CRLF (D-47)", () =>
            ClipboardIO.ReadRaw(hwnd)?.Plain == "selftest 한글 🎉\r\nline2" ? null : "got: " + Escape(ClipboardIO.ReadRaw(hwnd)?.Plain));
        Check("CF_HTML fragment round-trip (Korean/emoji offsets)", () =>
            ClipboardIO.ReadRaw(hwnd)?.Html == frag ? null : "got: " + Escape(ClipboardIO.ReadRaw(hwnd)?.Html));
        Check("CurrentHash equals hash of original LF text (D-49)", () =>
            ClipboardIO.CurrentTextHash(hwnd) == SyncLogic.ContentHash(text, frag) ? null : "hash differs");

        // 이미지: PNG + CF_DIBV5 를 함께 쓰고, 두 포맷을 각각 읽어 픽셀을 비교한다 (S-3 알파 램프)
        var ramp = Ramp(96, 64);
        var rampPng = Imaging.EncodePng(ramp);
        Check("write PNG + CF_DIBV5 with marker", () =>
        {
            ClipboardIO.Write(hwnd, [new BundleEntry(3, "", rampPng)], id, Imaging.BgraToDibV5(ramp));
            return null;
        });
        Check("PNG read back decodes (GDI+) to same pixels", () =>
            ClipboardIO.ReadFormat(hwnd, ClipboardIO.FmtPng) is { } p && ImageCodec.DecodePng(p).Pixels.SequenceEqual(ramp.Pixels) ? null : "pixels differ");
        Check("CF_DIBV5 read back keeps straight alpha", () =>
            ClipboardIO.ReadFormat(hwnd, Native.CF_DIBV5) is { } d && Imaging.DibToBgra(d) is { } b && b.Pixels.SequenceEqual(ramp.Pixels) ? null : "pixels differ");
        Check("image read prefers PNG, pixel hash stable", () =>
        {
            var raw = ClipboardIO.ReadImage(hwnd, out _, allowOwn: true);
            if (raw is null || !raw.IsPng) return "PNG not preferred";
            return ImageCodec.Normalize(raw) is { } n && Imaging.PixelHash(n.Pixels) == Imaging.PixelHash(ramp) ? null : "hash differs";
        });

        var settings = Settings.Load();
        string? pass = null;
        try { pass = CredentialStore.Load(); } catch { /* 아래에서 skipped */ }
        if (settings.ServerUri is { } url && pass is not null)
        {
            using var client = new ServerClient(url, Protocol.DeriveKeys(pass), settings.DeviceId);
            Check($"server list ({url.Host})", () => { client.ListAsync(0).GetAwaiter().GetResult(); return null; });
            Check("websocket hello", () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var e = client.EventsAsync(ct: cts.Token).GetAsyncEnumerator();
                try
                {
                    if (!e.MoveNextAsync().AsTask().GetAwaiter().GetResult()) return "stream ended";
                    return e.Current is ServerEvent.Hello h ? null : "first event: " + e.Current;
                }
                finally { cts.Cancel(); e.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            });
        }
        else lines.Add("SKIP  server checks (not configured yet)");

        var summary = $"ClipSync {Application.ProductVersion} selftest: {(failed == 0 ? "ALL PASS" : failed + " FAILED")}\n\n" + string.Join("\n", lines);
        Log.Info(summary.Replace("\n", " | "));
        MessageBox.Show(summary, "ClipSync selftest", MessageBoxButtons.OK, failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        return failed == 0 ? 0 : 1;
    }

    static Bgra Ramp(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                px[i] = 128; px[i + 1] = (byte)(255 - y * 3); px[i + 2] = (byte)(20 + y * 3); px[i + 3] = (byte)(x * 255 / (w - 1));
            }
        return new Bgra(w, h, px);
    }

    static string Escape(string? s) => s is null ? "(null)" : s.Replace("\r", "\\r").Replace("\n", "\\n");
}
