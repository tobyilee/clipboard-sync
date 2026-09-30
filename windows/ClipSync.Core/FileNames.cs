using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ClipSync.Core;

/// 수신 파일명 정리 (spec D-57). protocol/ref/src/protocol.ts sanitizeFileName 과 같은 규칙이며 test-vectors.json 으로 확인한다.
public static class FileNames
{
    static readonly HashSet<string> ReservedStems =
        ["con", "prn", "aux", "nul", .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"com{i}", $"lpt{i}" })];
    const string Bad = "/\\<>:\"|?*";
    const int MaxUtf16 = 150, MaxUtf8 = 240;

    static string StripTrailing(string s) => s.TrimEnd('.', ' ');
    static bool Fits(string s) => s.Length <= MaxUtf16 && Encoding.UTF8.GetByteCount(s) <= MaxUtf8;

    public static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (var r in name.Normalize(NormalizationForm.FormC).EnumerateRunes())
            sb.Append(r.Value < 0x20 || r.Value == 0x7F || (r.IsAscii && Bad.Contains((char)r.Value)) ? "_" : r.ToString());
        var s = StripTrailing(sb.ToString().Trim(' '));
        if (s.Length == 0) return "file";
        var firstDot = s.IndexOf('.');
        var stem = (firstDot >= 0 ? s[..firstDot] : s).TrimEnd(' ');
        if (ReservedStems.Contains(stem.ToLowerInvariant())) s = "_" + s;
        if (Fits(s)) return s;
        var lastDot = s.LastIndexOf('.');
        var ext = lastDot > 0 ? s[lastDot..] : "";
        if (ext.Length > 20) ext = "";
        var runes = (ext.Length > 0 ? s[..lastDot] : s).EnumerateRunes().ToList();
        while (runes.Count > 0 && !Fits(string.Concat(runes.Select(r => r.ToString())) + ext)) runes.RemoveAt(runes.Count - 1);
        var b = StripTrailing(string.Concat(runes.Select(r => r.ToString())));
        return (b.Length == 0 ? "file" : b) + ext;
    }

    /// 한 항목 안의 이름들을 정리하고, 대소문자 무시 중복이면 `이름 (2).ext`처럼 번호를 붙인다.
    public static List<string> Unique(IEnumerable<string> names)
    {
        var used = new HashSet<string>();
        var result = new List<string>();
        foreach (var n in names)
        {
            var s = Sanitize(n);
            var dot = s.LastIndexOf('.');
            var (b, ext) = dot > 0 ? (s[..dot], s[dot..]) : (s, "");
            var o = s;
            for (var i = 2; used.Contains(o.ToLowerInvariant()); i++) o = $"{b} ({i}){ext}";
            used.Add(o.ToLowerInvariant());
            result.Add(o);
        }
        return result;
    }

    /// D-54: 파일 항목의 에코 방지 해시 = (NFC 이름, 크기, 내용 SHA-256) 목록(이름순)의 해시.
    public static string FilesHash(IEnumerable<(string Name, byte[] Data)> files)
    {
        var lines = files.Select(f => $"{f.Name.Normalize(NormalizationForm.FormC)}\t{f.Data.Length}\t{Convert.ToHexStringLower(SHA256.HashData(f.Data))}")
                         .OrderBy(l => l, StringComparer.Ordinal);
        return "files:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }
}

/// Windows `CF_HDROP` 페이로드 (DROPFILES 20바이트 + 경로 목록, 이중 NUL). Mac에서도 테스트할 수 있게 Core에 둔다.
public static class HDrop
{
    public static List<string> Parse(byte[] d)
    {
        if (d.Length < 20) return [];
        var off = BinaryPrimitives.ReadInt32LittleEndian(d);
        var wide = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(16)) != 0;
        if (off < 20 || off > d.Length) return [];
        var text = wide ? Encoding.Unicode.GetString(d, off, (d.Length - off) & ~1) : Encoding.Default.GetString(d, off, d.Length - off);
        var paths = new List<string>();
        foreach (var p in text.Split('\0'))
        {
            if (p.Length == 0) break;   // 빈 문자열 = 목록 끝 (이중 NUL)
            paths.Add(p);
        }
        return paths;
    }

    /// DROPFILES(pFiles=20, pt=0, fNC=0, fWide=1) + UTF-16 경로들 + 이중 NUL.
    public static byte[] Build(IEnumerable<string> paths)
    {
        var body = Encoding.Unicode.GetBytes(string.Concat(paths.Select(p => p + "\0")) + "\0");
        var d = new byte[20 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(d, 20);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(16), 1);
        body.CopyTo(d, 20);
        return d;
    }
}
