using System.Runtime.InteropServices;
using System.Text;

record FormatItem(uint Id, string Name, byte[]? Data, long Size, string? Note);

// Raw Win32 clipboard I/O. No WinForms Clipboard/DataObject: we want to see exactly which ids/names/bytes exist.
static class Clip
{
    public const uint CF_TEXT = 1, CF_BITMAP = 2, CF_DIB = 8, CF_UNICODETEXT = 13, CF_HDROP = 15, CF_DIBV5 = 17;
    public static readonly uint FmtHtml, FmtPng, FmtMarker, FmtDropEffect, FmtNoCloud;

    static readonly Dictionary<uint, string> Std = new()
    {
        [1] = "CF_TEXT", [2] = "CF_BITMAP", [3] = "CF_METAFILEPICT", [4] = "CF_SYLK", [5] = "CF_DIF", [6] = "CF_TIFF",
        [7] = "CF_OEMTEXT", [8] = "CF_DIB", [9] = "CF_PALETTE", [10] = "CF_PENDATA", [11] = "CF_RIFF", [12] = "CF_WAVE",
        [13] = "CF_UNICODETEXT", [14] = "CF_ENHMETAFILE", [15] = "CF_HDROP", [16] = "CF_LOCALE", [17] = "CF_DIBV5",
    };

    static Clip()
    {
        FmtHtml = Native.RegisterClipboardFormat("HTML Format");
        FmtPng = Native.RegisterClipboardFormat("PNG");
        FmtMarker = Native.RegisterClipboardFormat("application/x-clipsync-marker");
        FmtDropEffect = Native.RegisterClipboardFormat("Preferred DropEffect");
        FmtNoCloud = Native.RegisterClipboardFormat("CanUploadToCloudClipboard");
    }

    public static string FormatName(uint id)
    {
        if (Std.TryGetValue(id, out var s)) return s;
        var sb = new StringBuilder(256);
        return Native.GetClipboardFormatName(id, sb, sb.Capacity) > 0 ? sb.ToString() : $"#{id}";
    }

    // spec 8: 5 tries, 20ms -> 320ms backoff
    static bool OpenWithRetry(IntPtr hwnd, out int attempts)
    {
        var delay = 20;
        for (attempts = 1; ; attempts++)
        {
            if (Native.OpenClipboard(hwnd)) return true;
            if (attempts == 5) return false;
            Thread.Sleep(delay);
            delay *= 2;
        }
    }

    public static List<FormatItem>? Capture(IntPtr hwnd, out int openAttempts, long capBytes = 32L << 20)
    {
        if (!OpenWithRetry(hwnd, out openAttempts)) return null;
        var list = new List<FormatItem>();
        try
        {
            for (uint f = Native.EnumClipboardFormats(0); f != 0; f = Native.EnumClipboardFormats(f))
            {
                var name = FormatName(f);
                // GDI handle formats are not HGLOBAL
                if (f == 2 || f == 3 || f == 9 || f == 14 || (f >= 0x200 && f <= 0x3FF))
                {
                    list.Add(new(f, name, null, 0, "GDI/handle format (not read)"));
                    continue;
                }
                var h = Native.GetClipboardData(f);
                if (h == IntPtr.Zero)
                {
                    list.Add(new(f, name, null, 0, $"GetClipboardData null (err {Marshal.GetLastWin32Error()})"));
                    continue;
                }
                var size = (long)Native.GlobalSize(h);
                var p = Native.GlobalLock(h);
                if (p == IntPtr.Zero)
                {
                    list.Add(new(f, name, null, size, $"GlobalLock failed (err {Marshal.GetLastWin32Error()})"));
                    continue;
                }
                var n = (int)Math.Min(size, capBytes);
                var buf = new byte[n];
                Marshal.Copy(p, buf, 0, n);
                Native.GlobalUnlock(h);
                list.Add(new(f, name, buf, size, size > capBytes ? "truncated" : null));
            }
        }
        finally { Native.CloseClipboard(); }
        return list;
    }

    static IntPtr Alloc(byte[] data)
    {
        var h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)(uint)Math.Max(1, data.Length));
        var p = Native.GlobalLock(h);
        Marshal.Copy(data, 0, p, data.Length);
        Native.GlobalUnlock(h);
        return h;
    }

    // Every write carries the private marker (echo-prevention premise) and CanUploadToCloudClipboard=0 (spec 8).
    public static void Write(IntPtr hwnd, byte[] markerId, IEnumerable<(uint fmt, byte[] data)> items)
    {
        if (!OpenWithRetry(hwnd, out _)) throw new InvalidOperationException("OpenClipboard failed after retries");
        try
        {
            Native.EmptyClipboard();
            var all = items.Concat(new[]
            {
                (FmtMarker, markerId),
                (FmtNoCloud, BitConverter.GetBytes(0)),
            });
            foreach (var (fmt, data) in all)
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
    }

    public static byte[] Utf16Z(string s) => Encoding.Unicode.GetBytes(s + "\0");

    public static string OwnerName()
    {
        try
        {
            var o = Native.GetClipboardOwner();
            if (o == IntPtr.Zero) return "(none)";
            Native.GetWindowThreadProcessId(o, out var pid);
            return $"{System.Diagnostics.Process.GetProcessById((int)pid).ProcessName}({pid})";
        }
        catch (Exception e) { return "(unknown: " + e.GetType().Name + ")"; }
    }
}
