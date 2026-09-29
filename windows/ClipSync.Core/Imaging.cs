using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace ClipSync.Core;

/// 32bpp straight-alpha BGRA 픽셀 (위→아래 행 순서).
public sealed record Bgra(int Width, int Height, byte[] Pixels);

/// Windows DIB → 픽셀, 픽셀 → PNG (System.Drawing 없이; Mac에서도 테스트 가능). S-3 스파이크의 검증된 파서를 정리.
public static class Imaging
{
    static byte Extract(uint p, uint mask)
    {
        if (mask == 0) return 255;
        var shift = BitOperations.TrailingZeroCount(mask);
        var bits = BitOperations.PopCount(mask >> shift);
        var v = (p & mask) >> shift;
        return bits == 8 ? (byte)v : (byte)(v * 255 / ((1u << bits) - 1));
    }

    /// CF_DIB / CF_DIBV5 페이로드 → BGRA. top-down(음수 높이), 40바이트 헤더 뒤 BI_BITFIELDS 마스크, V4/V5 알파 마스크를 처리한다.
    /// 32bpp인데 알파가 전부 0이면 불투명으로 본다 (S-3). 팔레트/RLE/16bpp는 null.
    public static Bgra? DibToBgra(byte[] d)
    {
        if (d.Length < 40) return null;
        int biSize = BinaryPrimitives.ReadInt32LittleEndian(d), w = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4)), hRaw = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(8));
        int bpp = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(14)), comp = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(16)), clrUsed = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(32));
        if (w <= 0 || hRaw == 0 || w > 32768 || Math.Abs(hRaw) > 32768) return null;
        if (comp != 0 && comp != 3 && comp != 6) return null;
        if (bpp != 24 && bpp != 32) return null;
        var topDown = hRaw < 0;
        var h = Math.Abs(hRaw);

        uint rm = 0x00FF0000, gm = 0x0000FF00, bm = 0x000000FF, am = 0;
        long pix = biSize;
        if (comp == 3 || comp == 6)
        {
            if (d.Length < 56) return null;
            rm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(40)); gm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(44)); bm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(48));
            if (biSize >= 56) am = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(52));
            else
            {
                pix += 12;
                if (comp == 6) { am = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(52)); pix += 4; }
            }
        }
        else if (biSize >= 108 && bpp == 32) am = 0xFF000000;   // V4/V5 BI_RGB: 알파 바이트가 실제일 수 있음 (전부 0이면 아래 규칙)
        pix += (long)clrUsed * 4;
        var stride = (w * bpp + 31) / 32 * 4;
        if (pix + (long)stride * h > d.Length) return null;

        var px = new byte[w * h * 4];
        var anyAlpha = false;
        for (var y = 0; y < h; y++)
        {
            var row = pix + (long)(topDown ? y : h - 1 - y) * stride;
            for (var x = 0; x < w; x++)
            {
                byte r, g, b, a;
                if (bpp == 32)
                {
                    var p = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)(row + x * 4)));
                    r = Extract(p, rm); g = Extract(p, gm); b = Extract(p, bm); a = am == 0 ? (byte)255 : Extract(p, am);
                }
                else { b = d[row + x * 3]; g = d[row + x * 3 + 1]; r = d[row + x * 3 + 2]; a = 255; }
                var o = (y * w + x) * 4;
                px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = a;
                if (a != 0) anyAlpha = true;
            }
        }
        if (am != 0 && !anyAlpha) for (var i = 3; i < px.Length; i += 4) px[i] = 255;
        return new Bgra(w, h, px);
    }

    /// BGRA → CF_DIBV5 (BITMAPV5HEADER 124B, 32bpp BI_BITFIELDS + 알파 마스크, bottom-up, straight alpha). S-3에서 알파 왕복 확인.
    public static byte[] BgraToDibV5(Bgra img)
    {
        var (w, h, px) = (img.Width, img.Height, img.Pixels);
        var o = new byte[124 + w * h * 4];
        var s = o.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, 124);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], w);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], h);
        BinaryPrimitives.WriteInt16LittleEndian(s[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 3);          // BI_BITFIELDS
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], w * h * 4);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], 2835);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], 2835);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(s[48..], 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(s[52..], 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(s[56..], 0x73524742); // LCS_sRGB
        BinaryPrimitives.WriteInt32LittleEndian(s[108..], 4);          // LCS_GM_IMAGES
        for (var y = 0; y < h; y++) px.AsSpan(y * w * 4, w * 4).CopyTo(s[(124 + (h - 1 - y) * w * 4)..]);
        return o;
    }

    /// BGRA → PNG (RGBA8, 필터 0, zlib). 정규 포맷 PNG로 보내기 위함 (spec 4.3).
    public static byte[] EncodePng(Bgra img)
    {
        var (w, h, px) = (img.Width, img.Height, img.Pixels);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var line = new byte[1 + w * 4];
            for (var y = 0; y < h; y++)
            {
                line[0] = 0;
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 4;
                    var j = 1 + x * 4;
                    line[j] = px[i + 2]; line[j + 1] = px[i + 1]; line[j + 2] = px[i]; line[j + 3] = px[i + 3];
                }
                z.Write(line);
            }
        }
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 6;   // 8bit RGBA
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(u32, data.Length); s.Write(u32);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(data);
        var crc = Crc32([.. t, .. data]);
        BinaryPrimitives.WriteUInt32BigEndian(u32, crc); s.Write(u32);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// PNG 청크 CRC (ISO 3309). NuGet 의존성을 늘리지 않으려고 직접 구현.
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    /// D-54: 디코드한 픽셀 해시 (같은 기기 안에서만 비교). 완전 투명 픽셀의 색은 무시한다.
    public static string PixelHash(Bgra img)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.ASCII.GetBytes($"img:{img.Width}x{img.Height}:"));
        var px = (byte[])img.Pixels.Clone();
        for (var i = 0; i < px.Length; i += 4) if (px[i + 3] == 0) px[i] = px[i + 1] = px[i + 2] = 0;
        sha.AppendData(px);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
