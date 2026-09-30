using System.Text;
using System.Text.Json;
using ClipSync.Core;
using Xunit;

namespace ClipSync.Core.Tests;

// Mirrors protocol/ref/test/vectors.test.ts against protocol/test-vectors.json (found by walking up from the test assembly).
public class VectorTests
{
    static readonly JsonElement V = Load();

    static JsonElement Load()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "protocol", "test-vectors.json"))) d = d.Parent;
        if (d == null) throw new FileNotFoundException("protocol/test-vectors.json not found above " + AppContext.BaseDirectory);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(d.FullName, "protocol", "test-vectors.json"))).RootElement.Clone();
    }

    static byte[] H(string hex) => Convert.FromHexString(hex);
    static string S(JsonElement e, string k) => e.GetProperty(k).GetString()!;
    static string Hex(byte[] b) => Convert.ToHexStringLower(b);
    static List<BundleEntry> Entries(JsonElement a) =>
        a.EnumerateArray().Select(e => new BundleEntry((byte)e.GetProperty("type").GetInt32(), S(e, "name"), H(S(e, "data_hex")))).ToList();
    static void AssertEntries(List<BundleEntry> want, List<BundleEntry> got, string name)
    {
        Assert.True(want.Count == got.Count, name + ": count");
        for (var i = 0; i < want.Count; i++)
        {
            Assert.True(want[i].Type == got[i].Type, name + ": type");
            Assert.True(want[i].Name == got[i].Name, name + ": name");
            Assert.True(want[i].Data.SequenceEqual(got[i].Data), name + ": data");
        }
    }

    [Fact]
    public void Passphrases()
    {
        foreach (var p in V.GetProperty("passphrases").EnumerateArray())
        {
            var n = S(p, "name");
            var k = Protocol.DeriveKeys(S(p, "passphrase"));
            Assert.True(Hex(k.PassNorm) == S(p, "pass_norm_hex"), n + " pass_norm");
            Assert.True(Hex(k.Master) == S(p, "master"), n + " master");
            Assert.True(Hex(k.EncKey) == S(p, "enc_key"), n + " enc_key");
            Assert.True(Hex(k.AuthToken) == S(p, "auth_token"), n + " auth_token");
            Assert.True(k.VaultId == S(p, "vault_id"), n + " vault_id");
        }
        string Vid(string n) => V.GetProperty("passphrases").EnumerateArray().First(p => S(p, "name") == n).GetProperty("vault_id").GetString()!;
        Assert.Equal(Vid("ascii"), Vid("ws-variant-of-ascii"));
        Assert.NotEqual(Vid("ascii"), Vid("case-differs"));
        Assert.NotEqual(Vid("ascii"), Vid("zwsp-feff-not-whitespace"));
    }

    [Fact]
    public void NormalizePassphraseEdgeCases()
    {
        Assert.Equal("", Protocol.NormalizePassphrase("   "));
        Assert.Equal("a b", Protocol.NormalizePassphrase("a\u3000\u3000b"));
        Assert.Equal("a b", Protocol.NormalizePassphrase(" a  b "));
        Assert.Equal("a\u200bb", Protocol.NormalizePassphrase("a\u200bb"));
        Assert.Equal("a\ufeffb", Protocol.NormalizePassphrase("a\ufeffb"));
        Assert.Equal("a", Protocol.NormalizePassphrase("\u2028a\u2029"));
    }

    [Fact]
    public void Uuids()
    {
        foreach (var u in V.GetProperty("uuids").EnumerateArray())
        {
            var b = Protocol.ParseUuid(S(u, "input"));
            Assert.Equal(S(u, "bytes_hex"), Hex(b));
            Assert.Equal(S(u, "canonical"), Protocol.FormatUuid(b));
            Assert.Equal(S(u, "hex_id"), Protocol.UuidHex(b));
        }
    }

    [Fact]
    public void Seals()
    {
        byte[] KeyOf(string n) => H(V.GetProperty("passphrases").EnumerateArray().First(p => S(p, "name") == n).GetProperty("enc_key").GetString()!);
        foreach (var s in V.GetProperty("seals").EnumerateArray())
        {
            var n = S(s, "name");
            var part = S(s, "part");
            var aad = part == "config"
                ? Protocol.ConfigAad(ulong.Parse(S(s, "config_version")))
                : Protocol.ItemAad(Protocol.ParseUuid(S(s, "item_id_uuid")), Protocol.ParseUuid(S(s, "device_id_uuid")), ulong.Parse(S(s, "created_at")), (byte)(part == "header" ? 1 : 2));
            Assert.True(Hex(aad) == S(s, "aad_hex"), n + " aad");
            var key = KeyOf(S(s, "key_from"));
            Assert.True(Hex(Protocol.SealWithNonce(key, H(S(s, "nonce")), aad, H(S(s, "plaintext_hex")))) == S(s, "sealed_hex"), n + " sealed");
            var pt = Protocol.Open(key, aad, H(S(s, "sealed_hex")));
            Assert.True(Hex(pt) == S(s, "plaintext_hex"), n + " plaintext");
            if (s.TryGetProperty("expect_json", out var expect))
            {
                // compare parsed fields, never encoder bytes
                using var actual = JsonDocument.Parse(Encoding.UTF8.GetString(pt));
                Assert.True(JsonEq(expect, actual.RootElement), n + " expect_json");
            }
        }
    }

    static bool JsonEq(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind && !(a.ValueKind is JsonValueKind.True or JsonValueKind.False && b.ValueKind is JsonValueKind.True or JsonValueKind.False)) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var pa = a.EnumerateObject().ToList();
                if (pa.Count != b.EnumerateObject().Count()) return false;
                return pa.All(p => b.TryGetProperty(p.Name, out var o) && JsonEq(p.Value, o));
            case JsonValueKind.Array:
                var la = a.EnumerateArray().ToList(); var lb = b.EnumerateArray().ToList();
                return la.Count == lb.Count && la.Zip(lb).All(t => JsonEq(t.First, t.Second));
            case JsonValueKind.String: return a.GetString() == b.GetString();
            case JsonValueKind.Number: return a.GetDecimal() == b.GetDecimal();
            default: return a.GetRawText() == b.GetRawText();
        }
    }

    [Fact]
    public void SealRandomNonceRoundtrips()
    {
        var key = H(S(V.GetProperty("passphrases")[0], "enc_key"));
        var aad = H(S(V.GetProperty("seals")[0], "aad_hex"));
        var a = Protocol.Seal(key, aad, [1]); var b = Protocol.Seal(key, aad, [1]);
        Assert.NotEqual(Hex(a), Hex(b));
        Assert.Equal(new byte[] { 1 }, Protocol.Open(key, aad, a));
    }

    [Fact]
    public void Negatives()
    {
        foreach (var n in V.GetProperty("negatives").EnumerateArray())
        {
            Assert.Equal("fail", S(n, "expect"));
            Assert.ThrowsAny<ProtocolException>(() => Protocol.Open(H(S(n, "key_hex")), H(S(n, "aad_hex")), H(S(n, "sealed_hex"))));
        }
    }

    [Fact]
    public void Bundles()
    {
        foreach (var b in V.GetProperty("bundles").EnumerateArray())
        {
            var n = S(b, "name");
            AssertEntries(Entries(b.GetProperty("entries")), Protocol.DecodeBundle(H(S(b, "bytes_hex"))), n);
            var decodeOnly = b.TryGetProperty("decode_only", out var d) && d.GetBoolean();
            if (decodeOnly) continue;
            var input = b.TryGetProperty("encode_input", out var ei) ? ei : b.GetProperty("entries");
            Assert.True(Hex(Protocol.EncodeBundle(Entries(input))) == S(b, "bytes_hex"), n + " encode");
        }
    }

    [Fact]
    public void BundleInvalid()
    {
        foreach (var b in V.GetProperty("bundle_invalid").EnumerateArray())
        {
            Assert.Equal("error", S(b, "expect"));
            Assert.ThrowsAny<ProtocolException>(() => Protocol.DecodeBundle(H(S(b, "bytes_hex"))));
        }
    }

    [Fact]
    public void FileNames_()
    {
        foreach (var f in V.GetProperty("file_names").EnumerateArray())
            Assert.True(FileNames.Sanitize(S(f, "input")) == S(f, "expect"), S(f, "name") + ": got " + FileNames.Sanitize(S(f, "input")));
        foreach (var f in V.GetProperty("file_name_sets").EnumerateArray())
            Assert.Equal(f.GetProperty("expect").EnumerateArray().Select(e => e.GetString()!).ToList(),
                         FileNames.Unique(f.GetProperty("inputs").EnumerateArray().Select(e => e.GetString()!)));
    }

    [Fact]
    public void Previews()
    {
        foreach (var p in V.GetProperty("previews").EnumerateArray())
            Assert.True(Protocol.PreviewOf(S(p, "input")) == S(p, "output"), S(p, "name"));
    }
}
