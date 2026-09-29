using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipSync.Core;

/// vault 설정 (spec 4.4). 없거나 못 받으면 텍스트만 (fail-closed). mac VaultConfig.swift 와 같은 규칙.
public sealed record VaultConfig
{
    public const int Mib = 1 << 20;
    public const int DefaultMediaBytes = 20 * Mib;
    public const int TextLimit = 1 * Mib;
    public static readonly int[] MediaSizes = [5 * Mib, 10 * Mib, 20 * Mib, 50 * Mib];
    public static readonly VaultConfig FailClosed = new();

    [JsonPropertyName("v")] public int V { get; init; } = 1;
    [JsonPropertyName("images")] public bool Images { get; init; }
    [JsonPropertyName("files")] public bool Files { get; init; }
    [JsonPropertyName("max_media_bytes")] public int MaxMediaBytes { get; init; } = DefaultMediaBytes;

    /// D-51: config 키 → header kinds.
    [JsonIgnore]
    public IReadOnlySet<string> AllowedKinds
    {
        get
        {
            var s = new HashSet<string> { "text", "html" };
            if (Images) s.Add("image");
            if (Files) s.Add("files");
            return s;
        }
    }

    public static VaultConfig Parse(ReadOnlySpan<byte> json)
    {
        var c = JsonSerializer.Deserialize<VaultConfig>(json) ?? FailClosed;
        return MediaSizes.Contains(c.MaxMediaBytes) ? c : c with { MaxMediaBytes = DefaultMediaBytes };   // 허용값이 아니면 기본값
    }

    /// D-50: 저장될 version(If-Match+1)의 AAD로 봉인.
    public byte[] Seal(byte[] key, long version) =>
        Protocol.Seal(key, Protocol.ConfigAad((ulong)version), JsonSerializer.SerializeToUtf8Bytes(this));

    /// 서버가 알려 준 version으로 연다. 실패하면 예외 (호출자는 무시하고 캐시 유지).
    public static VaultConfig Open(ConfigBlob blob, byte[] key) =>
        Parse(Protocol.Open(key, Protocol.ConfigAad((ulong)blob.Version), blob.Blob));
}

public enum SendDecision { Ignore, Files, Image, ImageWithText, Text }

public static class Media
{
    /// spec 4.5: 표현 존재 여부만으로 판별한다 (꺼진 타입은 내용을 읽지 않기 위해).
    public static SendDecision Classify(bool hasFiles, bool hasImage, bool hasText, VaultConfig config)
    {
        if (hasFiles) return config.Files ? SendDecision.Files : SendDecision.Ignore;   // 파일명 문자열로 폴백하지 않는다
        if (hasImage)
        {
            if (config.Images) return hasText ? SendDecision.ImageWithText : SendDecision.Image;
            return hasText ? SendDecision.Text : SendDecision.Ignore;                   // 이미지 표현만 버린다
        }
        return hasText ? SendDecision.Text : SendDecision.Ignore;
    }

    public static int PlainSizeLimit(bool hasMedia, VaultConfig config) => hasMedia ? config.MaxMediaBytes : VaultConfig.TextLimit;

    /// PNG IHDR에서 가로·세로 (header `image {w,h}`용, 디코드하지 않음).
    public static (int W, int H)? PngSize(ReadOnlySpan<byte> b)
    {
        ReadOnlySpan<byte> sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (b.Length < 24 || !b[..8].SequenceEqual(sig) || !b.Slice(12, 4).SequenceEqual("IHDR"u8)) return null;
        return ((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b[16..]), (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b[20..]));
    }
}
