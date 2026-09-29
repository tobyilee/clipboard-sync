using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using ClipSync.Core;

namespace ClipSync.App;

/// 메시지 전용 윈도우 + AddClipboardFormatListener → WM_CLIPBOARDUPDATE (spec 8). UI(STA) 스레드에서 만든다.
sealed class ClipboardListener : NativeWindow, IDisposable
{
    public event Action? Changed;

    public ClipboardListener()
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });   // HWND_MESSAGE
        if (!Native.AddClipboardFormatListener(Handle)) Log.Error($"AddClipboardFormatListener failed ({Marshal.GetLastWin32Error()})");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_CLIPBOARDUPDATE) Changed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Native.RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }
}

/// Win32 클립보드 읽기/쓰기 (spec 4.5, 6.4, 8). WinForms Clipboard 대신 raw API로 포맷을 정확히 다룬다 (S-3에서 검증).
/// 원칙: 바이트만 복사하고 곧바로 CloseClipboard. 변환·파일 읽기는 닫은 뒤에 한다 (열어 둔 동안 다른 앱이 모두 막힌다).
static class ClipboardIO
{
    /// 에코 방지 1차 마커 (D-44): 원격 항목을 쓸 때 item_id 16바이트를 함께 기록한다.
    public static readonly uint FmtMarker = Native.RegisterClipboardFormat("ClipSyncItemId");
    public static readonly uint FmtHtml = Native.RegisterClipboardFormat("HTML Format");
    public static readonly uint FmtPng = Native.RegisterClipboardFormat("PNG");
    public static readonly uint FmtNoCloud = Native.RegisterClipboardFormat("CanUploadToCloudClipboard");

    public sealed record Content(string? Plain, string? Html);

    /// 내용을 읽지 않고 표현 존재 여부만 본다 (spec 4.5 판별 입력).
    public sealed record Shape(bool Own, bool HasFiles, bool HasImage, bool HasText);

    /// 원본 이미지 바이트: PNG 우선, 없으면 CF_DIBV5, 그다음 CF_DIB (합성 CF_DIB는 알파가 255라 DIBV5를 먼저, S-3).
    public sealed record RawImage(byte[] Data, bool IsPng);

    public static uint SequenceNumber => Native.GetClipboardSequenceNumber();

    static Shape CurrentShape() => new(
        Native.IsClipboardFormatAvailable(FmtMarker),
        Native.IsClipboardFormatAvailable(Native.CF_HDROP),
        Native.IsClipboardFormatAvailable(FmtPng) || Native.IsClipboardFormatAvailable(Native.CF_DIBV5) || Native.IsClipboardFormatAvailable(Native.CF_DIB),
        Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT) || Native.IsClipboardFormatAvailable(FmtHtml));

    /// 표현 판별은 **클립보드를 연 상태에서** 한다. 열지 않고 IsClipboardFormatAvailable을 보면 다른 프로세스가 쓰는 도중
    /// (예: PNG는 들어갔고 마커는 아직)을 보게 되어, 우리가 쓴 항목을 마커 없는 새 복사로 오인한다 (M5a Cloud PC에서 실제 발생).
    /// 열지 못하면 null (busy).
    public static Shape? GetShape(IntPtr hwnd)
    {
        if (!OpenWithRetry(hwnd)) return null;
        try { return CurrentShape(); }
        finally { Native.CloseClipboard(); }
    }

    /// 자체 테스트용: 연 상태에서 마커를 본다.
    public static bool HasOwnMarker(IntPtr hwnd) => GetShape(hwnd)?.Own == true;

    /// spec 8: OpenClipboard 실패 시 5회, 20ms→320ms 백오프.
    static bool OpenWithRetry(IntPtr hwnd)
    {
        var delay = 20;
        for (var attempt = 1; ; attempt++)
        {
            if (Native.OpenClipboard(hwnd)) return true;
            if (attempt == 5) return false;
            Thread.Sleep(delay);
            delay *= 2;
        }
    }

    static byte[]? GetBytes(uint fmt)
    {
        var h = Native.GetClipboardData(fmt);
        if (h == IntPtr.Zero) return null;
        var size = (long)Native.GlobalSize(h);
        var p = Native.GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            var buf = new byte[size];
            Marshal.Copy(p, buf, 0, (int)size);
            return buf;
        }
        finally { Native.GlobalUnlock(h); }
    }

    static string? ReadUnicodeText()
    {
        var b = GetBytes(Native.CF_UNICODETEXT);
        if (b is null) return null;
        var s = Encoding.Unicode.GetString(b);
        var nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    /// 텍스트/HTML fragment. 열지 못하면 null과 busy=true.
    public static Content? ReadText(IntPtr hwnd, out bool busy)
    {
        busy = false;
        if (!OpenWithRetry(hwnd)) { busy = true; return null; }
        try
        {
            if (Native.IsClipboardFormatAvailable(FmtMarker)) return null;   // 판별 뒤 우리 항목으로 바뀌었으면 읽지 않는다
            var plain = Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT) ? ReadUnicodeText() : null;
            var html = Native.IsClipboardFormatAvailable(FmtHtml) && GetBytes(FmtHtml) is { } h ? CfHtml.ExtractFragment(h) : null;
            // D-48/D-41: plain text가 없으면 HTML에서 만든다 (RDP를 통과하는 것은 plain text이므로 해시 기준을 맞춘다).
            plain ??= html is null ? null : SyncLogic.PlainFromHtml(html);
            return plain is null && html is null ? null : new Content(plain, html);
        }
        finally { Native.CloseClipboard(); }
    }

    public static RawImage? ReadImage(IntPtr hwnd, out bool busy, bool allowOwn = false)
    {
        busy = false;
        if (!OpenWithRetry(hwnd)) { busy = true; return null; }
        try
        {
            if (!allowOwn && Native.IsClipboardFormatAvailable(FmtMarker)) return null;   // 판별 뒤 우리 항목으로 바뀌었으면 읽지 않는다
            if (Native.IsClipboardFormatAvailable(FmtPng) && GetBytes(FmtPng) is { } png) return new RawImage(png, true);
            if (Native.IsClipboardFormatAvailable(Native.CF_DIBV5) && GetBytes(Native.CF_DIBV5) is { } v5) return new RawImage(v5, false);
            if (Native.IsClipboardFormatAvailable(Native.CF_DIB) && GetBytes(Native.CF_DIB) is { } dib) return new RawImage(dib, false);
            return null;
        }
        finally { Native.CloseClipboard(); }
    }

    /// 마커 유무와 관계없이 현재 텍스트/HTML fragment (D-49 비교와 자체 테스트용).
    public static Content? ReadRaw(IntPtr hwnd)
    {
        if (!OpenWithRetry(hwnd)) return null;
        try
        {
            var plain = Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT) ? ReadUnicodeText() : null;
            var html = Native.IsClipboardFormatAvailable(FmtHtml) && GetBytes(FmtHtml) is { } h ? CfHtml.ExtractFragment(h) : null;
            return new Content(plain, html);
        }
        finally { Native.CloseClipboard(); }
    }

    /// 자체 테스트용: 특정 포맷의 바이트.
    public static byte[]? ReadFormat(IntPtr hwnd, uint fmt)
    {
        if (!OpenWithRetry(hwnd)) return null;
        try { return Native.IsClipboardFormatAvailable(fmt) ? GetBytes(fmt) : null; }
        finally { Native.CloseClipboard(); }
    }

    /// D-49용: 현재 클립보드 텍스트의 해시 (plain text 우선, 없으면 HTML에서 만든 plain).
    public static string? CurrentTextHash(IntPtr hwnd) =>
        ReadRaw(hwnd) is { } c ? SyncLogic.ContentHash(c.Plain ?? (c.Html is null ? null : SyncLogic.PlainFromHtml(c.Html)), c.Html) : null;

    static IntPtr Alloc(byte[] data)
    {
        var h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)(uint)Math.Max(1, data.Length));
        if (h == IntPtr.Zero) throw new OutOfMemoryException("GlobalAlloc failed");
        var p = Native.GlobalLock(h);
        Marshal.Copy(data, 0, p, data.Length);
        Native.GlobalUnlock(h);
        return h;
    }

    /// 원격 항목을 클립보드에 적용한다. 이미지는 PNG와 CF_DIBV5를 **둘 다** 쓴다(spec 8; dibV5는 호출자가 미리 만든다).
    /// 마커와 CanUploadToCloudClipboard=0을 항상 함께 쓴다 (D-44). 반환: 쓴 뒤의 시퀀스 번호.
    public static uint Write(IntPtr hwnd, IEnumerable<BundleEntry> entries, string itemIdHex, byte[]? dibV5 = null)
    {
        var items = new List<(uint, byte[])>();
        foreach (var e in entries)
        {
            switch (e.Type)
            {
                case 1: // D-47: Windows에 적용할 때만 단독 LF → CRLF
                    items.Add((Native.CF_UNICODETEXT, Encoding.Unicode.GetBytes(SyncLogic.ToCrlf(Encoding.UTF8.GetString(e.Data)) + "\0")));
                    break;
                case 2:
                    items.Add((FmtHtml, CfHtml.Build(Encoding.UTF8.GetString(e.Data))));
                    break;
                case 3:
                    items.Add((FmtPng, e.Data));
                    break;
                // 파일은 M5b
            }
        }
        if (dibV5 is not null) items.Add((Native.CF_DIBV5, dibV5));
        if (items.Count == 0) throw new InvalidOperationException("no supported entries");
        items.Add((FmtMarker, Convert.FromHexString(itemIdHex)));
        items.Add((FmtNoCloud, BitConverter.GetBytes(0)));

        if (!OpenWithRetry(hwnd)) throw new InvalidOperationException("OpenClipboard failed after retries");
        try
        {
            Native.EmptyClipboard();
            foreach (var (fmt, data) in items)
            {
                var h = Alloc(data);
                if (Native.SetClipboardData(fmt, h) == IntPtr.Zero)
                {
                    var err = Marshal.GetLastWin32Error();
                    Native.GlobalFree(h);
                    throw new InvalidOperationException($"SetClipboardData({fmt}) failed, err {err}");
                }
            }
        }
        finally { Native.CloseClipboard(); }
        return Native.GetClipboardSequenceNumber();
    }
}

/// PNG 디코드는 GDI+로 한다 (Windows 전용; 인코드·DIB 파싱은 Core). 32bppArgb = straight alpha BGRA.
static class ImageCodec
{
    public static Bgra DecodePng(byte[] png)
    {
        using var ms = new MemoryStream(png);
        using var bmp = new Bitmap(ms);
        var (w, h) = (bmp.Width, bmp.Height);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new byte[w * h * 4];
            for (var y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * w * 4, w * 4);
            return new Bgra(w, h, px);
        }
        finally { bmp.UnlockBits(d); }
    }

    /// 원본(PNG 또는 DIB) → (PNG 바이트, 픽셀). DIB는 Core 파서 + Core PNG 인코더 (알파 보존, spec 4.3).
    public static (byte[] Png, Bgra Pixels)? Normalize(ClipboardIO.RawImage raw)
    {
        if (raw.IsPng) return (raw.Data, DecodePng(raw.Data));
        var bgra = Imaging.DibToBgra(raw.Data);
        return bgra is null ? null : (Imaging.EncodePng(bgra), bgra);
    }
}
