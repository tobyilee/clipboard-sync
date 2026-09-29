// S-4 (C# side): verify PBKDF2/HKDF/AES-GCM against the shared vectors.
// Throwaway spike code. Usage: dotnet run [-- <path-to-vectors.json>]
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var path = args.Length > 0 ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../protocol/test-vectors.seed.json"));
if (!File.Exists(path)) path = Path.GetFullPath("../../../protocol/test-vectors.seed.json");
Console.WriteLine($"vectors: {path}");

var failures = 0;
void Check(string label, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + label); if (!ok) failures++; }
string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();
byte[] U64(string s) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, ulong.Parse(s)); return b; }

using var doc = JsonDocument.Parse(File.ReadAllText(path));
foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
{
    string S(string k) => v.GetProperty(k).GetString()!;
    var name = S("name");
    var text = S("passphrase");

    // NFKD -> UTF-8 bytes -> PBKDF2 (byte overload, never the string one)
    var pp = Encoding.UTF8.GetBytes(text.Normalize(NormalizationForm.FormKD));
    Check($"{name} NFKD bytes", Hex(pp) == S("passphrase_nfkd_utf8"));
    if (name == "nfkd") Check($"{name} NFKD differs from raw UTF-8", !pp.SequenceEqual(Encoding.UTF8.GetBytes(text)));

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var master = Rfc2898DeriveBytes.Pbkdf2(pp, Encoding.UTF8.GetBytes("clipsync/v1/salt"), 600_000, HashAlgorithmName.SHA256, 32);
    Console.WriteLine($"     pbkdf2 600k: {sw.ElapsedMilliseconds} ms");
    Check($"{name} master", Hex(master) == S("master"));

    var enc = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: Array.Empty<byte>(), info: Encoding.UTF8.GetBytes("clipsync/v1/enc"));
    var auth = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: Array.Empty<byte>(), info: Encoding.UTF8.GetBytes("clipsync/v1/auth"));
    Check($"{name} enc_key", Hex(enc) == S("enc_key"));
    Check($"{name} auth_token", Hex(auth) == S("auth_token"));
    Check($"{name} vault_id", Hex(SHA256.HashData(auth)) == S("vault_id"));

    // AAD rebuilt from fields (spec 3.3), not copied from the file
    var itemId = Convert.FromHexString(S("item_id"));
    var deviceId = Convert.FromHexString(S("device_id"));
    var created = U64(S("created_at"));
    var cfgVer = U64(S("config_version"));
    byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    var aads = new Dictionary<string, byte[]>
    {
        ["header"] = Cat([1], itemId, deviceId, created, [1]),
        ["body"] = Cat([1], itemId, deviceId, created, [2]),
        ["config"] = Cat([1], cfgVer, [3]),
    };

    foreach (var part in v.GetProperty("parts").EnumerateObject())
    {
        var label = $"{name}/{part.Name}";
        var p = part.Value;
        var aad = aads[part.Name];
        Check($"{label} AAD bytes", Hex(aad) == p.GetProperty("aad").GetString());

        var pt = Encoding.UTF8.GetBytes(p.GetProperty("plaintext").GetString()!);
        var nonce = Convert.FromHexString(p.GetProperty("nonce").GetString()!);
        using var gcm = new AesGcm(enc, 16);

        // seal: nonce || ciphertext || tag
        var ct = new byte[pt.Length]; var tag = new byte[16];
        gcm.Encrypt(nonce, pt, ct, tag, aad);
        var sealedMine = Cat(nonce, ct, tag);
        var expected = Convert.FromHexString(p.GetProperty("sealed").GetString()!);
        Check($"{label} seal == swift/node", sealedMine.SequenceEqual(expected));

        // open the reference ciphertext
        var rct = expected[12..^16]; var rtag = expected[^16..]; var outPt = new byte[rct.Length];
        var opened = false;
        try { gcm.Decrypt(expected[..12], rct, rtag, outPt, aad); opened = outPt.SequenceEqual(pt); } catch (CryptographicException) { }
        Check($"{label} open reference ciphertext", opened);

        // one flipped AAD byte must be rejected
        var bad = (byte[])aad.Clone(); bad[^1] ^= 1;
        var rejected = false;
        try { gcm.Decrypt(expected[..12], rct, rtag, new byte[rct.Length], bad); } catch (CryptographicException) { rejected = true; }
        Check($"{label} tampered AAD rejected", rejected);
    }
}
Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURES");
return failures == 0 ? 0 : 1;
