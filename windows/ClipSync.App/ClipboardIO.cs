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
static class ClipboardIO
{
    /// 에코 방지 1차 마커 (D-44): 원격 항목을 쓸 때 item_id 16바이트를 함께 기록한다.
    public static readonly uint FmtMarker = Native.RegisterClipboardFormat("ClipSyncItemId");
    public static readonly uint FmtHtml = Native.RegisterClipboardFormat("HTML Format");
    public static readonly uint FmtPng = Native.RegisterClipboardFormat("PNG");
    public static readonly uint FmtNoCloud = Native.RegisterClipboardFormat("CanUploadToCloudClipboard");

    public sealed record Content(string? Plain, string? Html);

    public abstract record ReadResult
    {
        public sealed record OwnMarker : ReadResult;
        public sealed record Ignored(string Why) : ReadResult;
        public sealed record Text(Content Value) : ReadResult;
        public sealed record Busy : ReadResult;
    }

    public static uint SequenceNumber => Native.GetClipboardSequenceNumber();

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

    /// 종류는 포맷 존재 여부로 먼저 판별하고, 꺼진 타입은 내용을 읽지 않는다 (spec 4.5).
    public static ReadResult Read(IntPtr hwnd)
    {
        if (Native.IsClipboardFormatAvailable(FmtMarker)) return new ReadResult.OwnMarker();
        // 4.5-1: 파일이 있으면 항목 전체 무시 (파일명 문자열로 폴백하지 않는다). 파일 동기화는 M5.
        if (Native.IsClipboardFormatAvailable(Native.CF_HDROP)) return new ReadResult.Ignored("files");
        var hasText = Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT);
        var hasHtml = Native.IsClipboardFormatAvailable(FmtHtml);
        if (!hasText && !hasHtml)
        {
            var image = Native.IsClipboardFormatAvailable(FmtPng) || Native.IsClipboardFormatAvailable(Native.CF_DIBV5) || Native.IsClipboardFormatAvailable(Native.CF_DIB);
            return new ReadResult.Ignored(image ? "image (off)" : "no text");
        }
        if (!OpenWithRetry(hwnd)) return new ReadResult.Busy();
        try
        {
            var plain = hasText ? ReadUnicodeText() : null;
            string? html = null;
            if (hasHtml && GetBytes(FmtHtml) is { } h) html = CfHtml.ExtractFragment(h);
            // D-48/D-41: plain text가 없으면 HTML에서 만든다 (RDP를 통과하는 것은 plain text이므로 해시 기준을 맞춘다).
            plain ??= html is null ? null : SyncLogic.PlainFromHtml(html);
            if (plain is null && html is null) return new ReadResult.Ignored("empty");
            return new ReadResult.Text(new Content(plain, html));
        }
        finally { Native.CloseClipboard(); }
    }

    /// 마커 유무와 관계없이 현재 텍스트/HTML fragment를 읽는다 (D-49 비교와 자체 테스트용). 열지 못하면 null.
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

    /// D-49용: 현재 클립보드 내용의 해시 (plain text 우선, 없으면 HTML에서 만든 plain).
    public static string? CurrentHash(IntPtr hwnd) =>
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

    /// 원격 항목을 클립보드에 적용한다. 마커와 CanUploadToCloudClipboard=0을 항상 함께 쓴다 (D-44, spec 8).
    /// 반환: 쓴 뒤의 시퀀스 번호 (감시자가 자기 쓰기를 건너뛰는 데 쓴다).
    public static uint Write(IntPtr hwnd, IEnumerable<BundleEntry> entries, string itemIdHex)
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
                // 이미지/파일은 M5
            }
        }
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
