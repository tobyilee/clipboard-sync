using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

static class Img
{
    public static byte[] GetBgra(Bitmap src, out int w, out int h)
    {
        w = src.Width; h = src.Height;
        var d = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new byte[w * h * 4];
            for (var y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * w * 4, w * 4);
            return px;
        }
        finally { src.UnlockBits(d); }
    }

    public static Bitmap FromBgra(byte[] px, int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (var y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, d.Scan0 + y * d.Stride, w * 4); }
        finally { b.UnlockBits(d); }
        return b;
    }

    public static byte[] ToPng(Bitmap b)
    {
        using var ms = new MemoryStream();
        b.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    // 96x64: alpha ramps 0..255 left to right (column 0 fully transparent, last column opaque), color varies by row.
    public static Bitmap MakeTestBitmap()
    {
        const int W = 96, H = 64;
        var b = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                b.SetPixel(x, y, Color.FromArgb(x * 255 / (W - 1), 20 + y * 3, 255 - y * 3, 128));
        return b;
    }

    // BITMAPV5HEADER (124 bytes), 32bpp BI_BITFIELDS with alpha mask, bottom-up rows, straight (non-premultiplied) alpha.
    public static byte[] BuildDibV5(Bitmap b)
    {
        var px = GetBgra(b, out var w, out var h);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(124); bw.Write(w); bw.Write(h); bw.Write((short)1); bw.Write((short)32);
        bw.Write(3); bw.Write(w * h * 4); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
        bw.Write(0x00FF0000u); bw.Write(0x0000FF00u); bw.Write(0x000000FFu); bw.Write(0xFF000000u);
        bw.Write(0x73524742u);          // LCS_sRGB
        bw.Write(new byte[36]);         // CIEXYZTRIPLE
        bw.Write(0); bw.Write(0); bw.Write(0);  // gamma
        bw.Write(4);                    // LCS_GM_IMAGES
        bw.Write(0); bw.Write(0); bw.Write(0);  // profile data/size, reserved
        for (var y = h - 1; y >= 0; y--) bw.Write(px, y * w * 4, w * 4);
        bw.Flush();
        return ms.ToArray();
    }

    static byte Extract(uint p, uint mask)
    {
        if (mask == 0) return 255;
        var shift = BitOperations.TrailingZeroCount(mask);
        var bits = BitOperations.PopCount(mask >> shift);
        var v = (p & mask) >> shift;
        return bits == 8 ? (byte)v : (byte)(v * 255 / ((1u << bits) - 1));
    }

    // CF_DIB / CF_DIBV5 payload -> PNG. Handles top-down (negative height), BI_BITFIELDS masks after a 40-byte header,
    // and 32bpp with all-zero alpha (treated as opaque). Palette / RLE / 16bpp are reported as unsupported.
    public static (byte[]? png, byte[]? bgra, int w, int h, string info) DibToPng(byte[] d)
    {
        if (d.Length < 40) return (null, null, 0, 0, "too short");
        int biSize = BitConverter.ToInt32(d, 0), w = BitConverter.ToInt32(d, 4), hRaw = BitConverter.ToInt32(d, 8);
        int bpp = BitConverter.ToInt16(d, 14), comp = BitConverter.ToInt32(d, 16), clrUsed = BitConverter.ToInt32(d, 32);
        var topDown = hRaw < 0;
        var h = Math.Abs(hRaw);
        var hdr = $"hdr={biSize} {w}x{hRaw} {bpp}bpp comp={comp} {(topDown ? "top-down" : "bottom-up")}";
        if (comp != 0 && comp != 3 && comp != 6) return (null, null, w, h, hdr + " UNSUPPORTED compression");
        if (bpp != 24 && bpp != 32) return (null, null, w, h, hdr + $" UNSUPPORTED bpp {bpp}");

        uint rm = 0x00FF0000, gm = 0x0000FF00, bm = 0x000000FF, am = 0;
        var pix = biSize;
        var bitfields = comp == 3 || comp == 6;
        if (bitfields)
        {
            if (biSize >= 56) { rm = BitConverter.ToUInt32(d, 40); gm = BitConverter.ToUInt32(d, 44); bm = BitConverter.ToUInt32(d, 48); am = BitConverter.ToUInt32(d, 52); }
            else
            {
                rm = BitConverter.ToUInt32(d, 40); gm = BitConverter.ToUInt32(d, 44); bm = BitConverter.ToUInt32(d, 48);
                pix += 12;
                if (comp == 6) { am = BitConverter.ToUInt32(d, 52); pix += 4; }
            }
        }
        else if (biSize >= 108 && bpp == 32) am = 0xFF000000;   // V4/V5 BI_RGB: alpha byte may be real; all-zero rule below
        pix += clrUsed * 4;
        var stride = (w * bpp + 31) / 32 * 4;
        if (pix + (long)stride * h > d.Length) return (null, null, w, h, hdr + $" TRUNCATED (need {pix + (long)stride * h}, have {d.Length})");

        var px = new byte[w * h * 4];
        long aMin = 255, aMax = 0, partial = 0, colorGtAlpha = 0;
        for (var y = 0; y < h; y++)
        {
            var sy = topDown ? y : h - 1 - y;
            var row = pix + sy * stride;
            for (var x = 0; x < w; x++)
            {
                byte r, g, b, a;
                if (bpp == 32)
                {
                    var p = BitConverter.ToUInt32(d, row + x * 4);
                    r = Extract(p, rm); g = Extract(p, gm); b = Extract(p, bm); a = am == 0 ? (byte)255 : Extract(p, am);
                }
                else { b = d[row + x * 3]; g = d[row + x * 3 + 1]; r = d[row + x * 3 + 2]; a = 255; }
                var o = (y * w + x) * 4;
                px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = a;
                if (a < aMin) aMin = a;
                if (a > aMax) aMax = a;
                if (a > 0 && a < 255) { partial++; if (r > a || g > a || b > a) colorGtAlpha++; }
            }
        }
        var note = "";
        if (am != 0 && aMax == 0)
        {
            for (var i = 3; i < px.Length; i += 4) px[i] = 255;
            note = " ALPHA-ALL-ZERO -> treated opaque";
        }
        var info = $"{hdr} masks={rm:X8}/{gm:X8}/{bm:X8}/{am:X8} alpha[{aMin}..{aMax}] partialAlphaPx={partial} colorGtAlphaPx={colorGtAlpha}{note}";
        using var bmp = FromBgra(px, w, h);
        return (ToPng(bmp), px, w, h, info);
    }

    // count of differing pixels; RGB ignored where both alphas are 0
    public static int Diff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return int.MaxValue;
        var n = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            if (a[i + 3] != b[i + 3]) { n++; continue; }
            if (a[i + 3] == 0) continue;
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) n++;
        }
        return n;
    }
}
