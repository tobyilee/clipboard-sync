using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClipSync.Core;

public sealed record Keys(byte[] PassNorm, byte[] Master, byte[] EncKey, byte[] AuthToken, string VaultId);
public sealed record BundleEntry(byte Type, string Name, byte[] Data);

// Protocol v1 (see protocol/PROTOCOL.md).
public static class Protocol
{
    // ---------- 2. key derivation ----------
    // Explicit whitespace set (PROTOCOL.md 2.1). Never char.IsWhiteSpace / regex \s.
    static bool IsWs(int cp) =>
        (cp >= 0x09 && cp <= 0x0D) || cp == 0x20 || cp == 0x85 || cp == 0xA0 || cp == 0x1680 ||
        (cp >= 0x2000 && cp <= 0x200A) || cp == 0x2028 || cp == 0x2029 || cp == 0x202F || cp == 0x205F || cp == 0x3000;

    public static string NormalizePassphrase(string input)
    {
        var sb = new StringBuilder();
        var pending = false;
        foreach (var r in input.Normalize(NormalizationForm.FormKD).EnumerateRunes())
        {
            if (IsWs(r.Value)) { pending = sb.Length > 0; continue; }
            if (pending) { sb.Append(' '); pending = false; }
            sb.Append(r.ToString());
        }
        return sb.ToString();
    }

    public static Keys DeriveKeys(string passphrase)
    {
        var passNorm = Encoding.UTF8.GetBytes(NormalizePassphrase(passphrase));
        var master = Rfc2898DeriveBytes.Pbkdf2(passNorm, Encoding.UTF8.GetBytes("clipsync/v1/salt"), 600_000, HashAlgorithmName.SHA256, 32);
        byte[] Hkdf(string info) => HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, Array.Empty<byte>(), Encoding.UTF8.GetBytes(info));
        var enc = Hkdf("clipsync/v1/enc");
        var auth = Hkdf("clipsync/v1/auth");
        return new Keys(passNorm, master, enc, auth, Convert.ToHexStringLower(SHA256.HashData(auth)));
    }

    // ---------- 3. identifiers (RFC 4122 order; never Guid.ToByteArray() default layout) ----------
    public static byte[] ParseUuid(string s)
    {
        if (s.Length != 36 || s[8] != '-' || s[13] != '-' || s[18] != '-' || s[23] != '-')
            throw new FormatException($"invalid uuid: {s}");
        var hex = s.Replace("-", "");
        foreach (var c in hex) if (!Uri.IsHexDigit(c)) throw new FormatException($"invalid uuid: {s}");
        return Convert.FromHexString(hex);
    }

    public static string FormatUuid(ReadOnlySpan<byte> b)
    {
        if (b.Length != 16) throw new ArgumentException("uuid must be 16 bytes");
        var h = Convert.ToHexStringLower(b);
        return $"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..]}";
    }

    public static string UuidHex(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(b);

    // ---------- 4. encryption ----------
    public const byte PartHeader = 1, PartBody = 2, PartConfig = 3;

    public static byte[] ItemAad(byte[] itemId, byte[] deviceId, ulong createdAtMs, byte part)
    {
        if (itemId.Length != 16 || deviceId.Length != 16) throw new ArgumentException("ids must be 16 bytes");
        if (part != PartHeader && part != PartBody) throw new ArgumentException("part must be 1 or 2");
        var b = new byte[1 + 16 + 16 + 8 + 1];
        b[0] = 1;
        itemId.CopyTo(b, 1);
        deviceId.CopyTo(b, 17);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(33), createdAtMs);
        b[41] = part;
        return b;
    }

    public static byte[] ConfigAad(ulong configVersion)
    {
        var b = new byte[10];
        b[0] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(1), configVersion);
        b[9] = PartConfig;
        return b;
    }

    // Test-only entry point: fixed nonces must never reach production code paths.
    internal static byte[] SealWithNonce(byte[] key, byte[] nonce, byte[] aad, byte[] plaintext)
    {
        if (nonce.Length != 12) throw new ArgumentException("nonce must be 12 bytes");
        var ct = new byte[plaintext.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plaintext, ct, tag, aad);
        var o = new byte[12 + ct.Length + 16];
        nonce.CopyTo(o, 0); ct.CopyTo(o, 12); tag.CopyTo(o, 12 + ct.Length);
        return o;
    }

    public static byte[] Seal(byte[] key, byte[] aad, byte[] plaintext) =>
        SealWithNonce(key, RandomNumberGenerator.GetBytes(12), aad, plaintext);

    public static byte[] Open(byte[] key, byte[] aad, byte[] sealed_)
    {
        if (sealed_.Length < 28) throw new ProtocolException("sealed data too short");
        var pt = new byte[sealed_.Length - 28];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(sealed_.AsSpan(0, 12), sealed_.AsSpan(12, pt.Length), sealed_.AsSpan(sealed_.Length - 16), pt, aad);
        }
        catch (CryptographicException e) { throw new ProtocolException("decryption failed", e); }
        return pt;
    }

    // ---------- 5. preview: NFC first, then 200 code points ----------
    public static string PreviewOf(string text)
    {
        var sb = new StringBuilder();
        var n = 0;
        foreach (var r in text.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (n++ == 200) break;
            sb.Append(r.ToString());
        }
        return sb.ToString();
    }

    // ---------- 6. bundle ----------
    static readonly byte[] Magic = "CSB1"u8.ToArray();

    public static byte[] EncodeBundle(IEnumerable<BundleEntry> entries)
    {
        var sorted = entries.OrderBy(e => e.Type).ToList(); // LINQ OrderBy is stable
        if (sorted.Count > 0xFFFF) throw new ProtocolException("too many entries");
        using var ms = new MemoryStream();
        ms.Write(Magic);
        ms.WriteByte(1);
        Span<byte> u16 = stackalloc byte[2], u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)sorted.Count); ms.Write(u16);
        foreach (var e in sorted)
        {
            var name = Encoding.UTF8.GetBytes(e.Name);
            if (e.Type is >= 1 and <= 3 && name.Length != 0) throw new ProtocolException($"type {e.Type} must have empty name");
            if (name.Length > 0xFFFF) throw new ProtocolException("name too long");
            ms.WriteByte(e.Type);
            BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)name.Length); ms.Write(u16);
            ms.Write(name);
            BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)e.Data.Length); ms.Write(u32);
            ms.Write(e.Data);
        }
        return ms.ToArray();
    }

    public static List<BundleEntry> DecodeBundle(byte[] b)
    {
        if (b.Length < 7) throw new ProtocolException("bundle truncated (header)");
        if (!b.AsSpan(0, 4).SequenceEqual(Magic)) throw new ProtocolException("bad magic");
        if (b[4] != 1) throw new ProtocolException($"unsupported bundle version {b[4]}");
        int count = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(5));
        long off = 7;
        var outList = new List<BundleEntry>();
        for (var i = 0; i < count; i++)
        {
            if (off + 3 > b.Length) throw new ProtocolException("bundle truncated (entry header)");
            var type = b[off];
            int nameLen = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan((int)off + 1));
            off += 3;
            if (off + nameLen + 4 > b.Length) throw new ProtocolException("bundle truncated (name)");
            string name;
            try { name = new UTF8Encoding(false, true).GetString(b, (int)off, nameLen); }
            catch (ArgumentException e) { throw new ProtocolException("invalid UTF-8 name", e); }
            off += nameLen;
            var dataLen = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)off));
            off += 4;
            if (off + dataLen > b.Length) throw new ProtocolException("bundle truncated (data)");
            var data = b.AsSpan((int)off, (int)dataLen).ToArray();
            off += dataLen;
            if (type is >= 1 and <= 3 && nameLen != 0) throw new ProtocolException($"type {type} must have empty name");
            if (type is >= 1 and <= 4) outList.Add(new BundleEntry(type, name, data)); // unknown types are skipped
        }
        if (off != b.Length) throw new ProtocolException("trailing bytes after last entry");
        return outList;
    }
}

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}
