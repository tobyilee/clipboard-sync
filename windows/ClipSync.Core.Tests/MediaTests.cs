using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ClipSync.Core;
using Xunit;

namespace ClipSync.Core.Tests;

public class MediaTests
{
    static readonly byte[] Key = Enumerable.Repeat((byte)7, 32).ToArray();

    [Fact]
    public void ClassifyFollowsSpec45()
    {
        var off = VaultConfig.FailClosed;
        var on = new VaultConfig { Images = true, Files = true };
        // Explorer/Finder 모양(파일 + 파일명 문자열 + 아이콘): 파일 off면 조용히 무시 — 파일명 텍스트로 새지 않는다
        Assert.Equal(SendDecision.Ignore, Media.Classify(true, true, true, off));
        Assert.Equal(SendDecision.Files, Media.Classify(true, true, true, on));
        Assert.Equal(SendDecision.Text, Media.Classify(false, true, true, off));
        Assert.Equal(SendDecision.Ignore, Media.Classify(false, true, false, off));
        Assert.Equal(SendDecision.ImageWithText, Media.Classify(false, true, true, on));
        Assert.Equal(SendDecision.Image, Media.Classify(false, true, false, on));
        Assert.Equal(SendDecision.Text, Media.Classify(false, false, true, off));
        Assert.Equal(SendDecision.Ignore, Media.Classify(false, false, false, on));
    }

    [Fact]
    public void AllowedKindsAndLimits()
    {
        Assert.Equal(new HashSet<string> { "text", "html" }, VaultConfig.FailClosed.AllowedKinds);
        Assert.Equal(new HashSet<string> { "text", "html", "image" }, new VaultConfig { Images = true }.AllowedKinds);
        Assert.Equal(new HashSet<string> { "text", "html", "files" }, new VaultConfig { Files = true }.AllowedKinds);
        Assert.Equal(1 << 20, Media.PlainSizeLimit(false, new VaultConfig { MaxMediaBytes = 50 << 20 }));
        Assert.Equal(5 << 20, Media.PlainSizeLimit(true, new VaultConfig { MaxMediaBytes = 5 << 20 }));
    }

    [Fact]
    public void ConfigJsonKeysClampAndVersionAad()
    {
        var cfg = new VaultConfig { Images = true, MaxMediaBytes = 10 << 20 };
        var sealed_ = cfg.Seal(Key, 4);
        Assert.Equal(cfg, VaultConfig.Open(new ConfigBlob(4, sealed_), Key));
        Assert.Throws<ProtocolException>(() => VaultConfig.Open(new ConfigBlob(5, sealed_), Key));   // 옛 blob을 새 version으로 끼우기 거부
        Assert.Contains("\"max_media_bytes\":10485760", Encoding.UTF8.GetString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(cfg)));
        Assert.Equal(VaultConfig.DefaultMediaBytes, VaultConfig.Parse("""{"v":1,"images":true,"max_media_bytes":12345}"""u8).MaxMediaBytes);
    }

    /// S-3의 알파 램프: 열 0은 완전 투명, 마지막 열은 불투명, 행마다 색이 다르다.
    internal static Bgra Ramp(int w = 96, int h = 64)
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

    [Fact]
    public void DibV5RoundTripKeepsStraightAlpha()
    {
        var img = Ramp();
        var back = Imaging.DibToBgra(Imaging.BgraToDibV5(img))!;
        Assert.Equal((img.Width, img.Height), (back.Width, back.Height));
        Assert.Equal(img.Pixels, back.Pixels);
    }

    static byte[] Dib24(int w, int h, bool topDown, Func<int, int, (byte b, byte g, byte r)> px)
    {
        var stride = (w * 24 + 31) / 32 * 4;
        var d = new byte[40 + stride * h];
        BinaryPrimitives.WriteInt32LittleEndian(d, 40);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(4), w);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(8), topDown ? -h : h);
        BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(14), 24);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var row = 40 + (topDown ? y : h - 1 - y) * stride;
                var (b, g, r) = px(x, y);
                d[row + x * 3] = b; d[row + x * 3 + 1] = g; d[row + x * 3 + 2] = r;
            }
        return d;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dib24BothRowOrders(bool topDown)
    {
        var d = Dib24(5, 3, topDown, (x, y) => ((byte)x, (byte)y, (byte)(x + y)));   // 행 끝 패딩 포함 (5*3=15 → stride 16)
        var img = Imaging.DibToBgra(d)!;
        Assert.Equal((5, 3), (img.Width, img.Height));
        var i = (2 * 5 + 4) * 4;   // (x=4, y=2) 위→아래 순서
        Assert.Equal(new byte[] { 4, 2, 6, 255 }, img.Pixels[i..(i + 4)]);
    }

    [Fact]
    public void Dib32AllZeroAlphaIsOpaque()
    {
        var v5 = Imaging.BgraToDibV5(new Bgra(2, 1, [1, 2, 3, 0, 4, 5, 6, 0]));
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, Imaging.DibToBgra(v5)!.Pixels);
    }

    [Fact]
    public void UnsupportedOrTruncatedDibIsNull()
    {
        Assert.Null(Imaging.DibToBgra(new byte[10]));
        var d = Dib24(4, 4, false, (_, _) => (0, 0, 0));
        Assert.Null(Imaging.DibToBgra(d[..60]));
        BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(14), 8);   // 팔레트
        Assert.Null(Imaging.DibToBgra(d));
    }

    [Fact]
    public void EncodedPngIsValidAndDecodesToSamePixels()
    {
        var img = Ramp(7, 5);
        var png = Imaging.EncodePng(img);
        Assert.Equal((7, 5), Media.PngSize(png));
        // 청크를 걷으며 CRC 확인, IDAT를 풀어 필터 0 + RGBA 행과 비교
        var off = 8;
        using var idat = new MemoryStream();
        while (off < png.Length)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(off));
            var type = Encoding.ASCII.GetString(png, off + 4, 4);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(off + 8 + len));
            Assert.Equal(crc, Imaging.Crc32(png.AsSpan(off + 4, 4 + len)));
            if (type == "IDAT") idat.Write(png, off + 8, len);
            off += 12 + len;
        }
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var r = raw.ToArray();
        Assert.Equal(5 * (1 + 7 * 4), r.Length);
        for (var y = 0; y < 5; y++)
            for (var x = 0; x < 7; x++)
            {
                var s = (y * 7 + x) * 4;
                var p = y * (1 + 28) + 1 + x * 4;
                Assert.Equal([img.Pixels[s + 2], img.Pixels[s + 1], img.Pixels[s], img.Pixels[s + 3]], r[p..(p + 4)]);
            }
        Assert.Equal(0xAE426082u, Imaging.Crc32("IEND"u8));   // 알려진 값
    }

    [Fact]
    public void PixelHashIgnoresColorOfFullyTransparentPixels()
    {
        var a = new Bgra(2, 1, [1, 2, 3, 0, 9, 9, 9, 255]);
        var b = new Bgra(2, 1, [7, 7, 7, 0, 9, 9, 9, 255]);
        Assert.Equal(Imaging.PixelHash(a), Imaging.PixelHash(b));
        Assert.NotEqual(Imaging.PixelHash(a), Imaging.PixelHash(new Bgra(2, 1, [1, 2, 3, 0, 9, 9, 8, 255])));
    }

    [Fact]
    public void WritePngForExternalCheck()
    {
        // Mac에서 sips로 열리는지 확인할 수 있게 남긴다 (테스트 자체는 항상 통과)
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "clipsync-csharp-ramp.png"), Imaging.EncodePng(Ramp()));
    }
}

public class FileTests
{
    [Fact]
    public void HDropRoundTripKoreanAndEmoji()
    {
        string[] paths = [@"C:\Users\x\AppData\Local\ClipboardSync\incoming\ab\한글 파일.txt", @"D:\a\😀 b.png"];
        var d = HDrop.Build(paths);
        Assert.Equal(20, BitConverter.ToInt32(d, 0));
        Assert.Equal(1, BitConverter.ToInt32(d, 16));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, d[^4..]);   // 마지막 경로 NUL + 목록 끝 NUL (UTF-16)
        Assert.Equal(paths, HDrop.Parse(d));
    }

    [Fact]
    public void HDropParseRejectsGarbage()
    {
        Assert.Empty(HDrop.Parse(new byte[5]));
        var d = HDrop.Build(["a"]);
        BitConverter.GetBytes(9999).CopyTo(d, 0);
        Assert.Empty(HDrop.Parse(d));
    }

    [Fact]
    public void FilesHashIgnoresOrderAndNormalizesNames()
    {
        var a = FileNames.FilesHash([("한글.txt".Normalize(System.Text.NormalizationForm.FormD), [1, 2]), ("b", [3])]);
        var b = FileNames.FilesHash([("b", [3]), ("한글.txt", [1, 2])]);
        Assert.Equal(a, b);
        Assert.NotEqual(a, FileNames.FilesHash([("b", [3]), ("한글.txt", [1, 3])]));
    }
}
