using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ClipSync.Core;

// mac ClipSyncCore/SyncLogic.swift 와 같은 규칙 (spec 6.3, 6.4, D-24, D-41, D-42, D-47, D-48).
public static partial class SyncLogic
{
    /// 해시 입력용 정규화: CRLF→LF, 끝의 NUL 제거, NFC. (본문은 바꾸지 않는다, 해시 계산에만 쓴다.)
    public static string NormalizeForHash(string s)
    {
        var t = s.Replace("\r\n", "\n");
        return t.TrimEnd('\0').Normalize(NormalizationForm.FormC);
    }

    /// D-41: 정규화한 plain text가 있으면 그것을, 없으면 정규화한 HTML을 해시한다.
    public static string? ContentHash(string? plainText, string? html)
    {
        var source = plainText ?? html;
        return source is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeForHash(source))));
    }

    /// D-47: Windows `CF_UNICODETEXT`에 적용할 때만 단독 LF를 CRLF로 바꾼다.
    public static string ToCrlf(string s) => LoneLf().Replace(s, "\r\n");

    [GeneratedRegex(@"(?<!\r)\n")] private static partial Regex LoneLf();

    /// D-48: `CF_UNICODETEXT` 없이 HTML만 있을 때의 plain text fallback (태그 제거 + 엔티티 해제).
    public static string? PlainFromHtml(string html)
    {
        var t = StyleOrScript().Replace(html, "");
        t = LineBreakTags().Replace(t, "\n");
        t = AnyTag().Replace(t, "");
        t = WebUtility.HtmlDecode(t);
        t = t.Replace(" ", " ").Trim();
        return t.Length == 0 ? null : t;
    }

    [GeneratedRegex(@"<(style|script)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex StyleOrScript();
    [GeneratedRegex(@"<br\s*/?>|</(p|div|li|tr|h[1-6])\s*>", RegexOptions.IgnoreCase)] private static partial Regex LineBreakTags();
    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)] private static partial Regex AnyTag();

    /// 최근 항목 20개 등에서 쓰는 catch-up 계획 (spec 6.3, D-42).
    /// holdLocal: 로컬 우선 규칙(D-58)이나 pending(D-59) 때문에 이번 catch-up에서는 원격 항목을 적용하지 않는다 (목록만 갱신).
    public static CatchUpPlan PlanCatchUp(IEnumerable<CatchUpCandidate> items, long lastSeq, string selfDevice, bool canApply, IReadOnlySet<string> allowedKinds, bool holdLocal = false)
    {
        var fresh = items.Where(i => i.Seq > lastSeq).ToList();
        var newLast = fresh.Count > 0 ? fresh.Max(i => i.Seq) : lastSeq;
        if (!canApply || holdLocal) return new CatchUpPlan(newLast, null);
        var target = fresh
            .Where(i => i.DeviceId != selfDevice && !i.Purged && i.Kinds.Count > 0 && i.Kinds.All(allowedKinds.Contains))
            .MaxBy(i => i.Seq);
        return new CatchUpPlan(newLast, target);
    }
}

public sealed record CacheEntry(string Id, DateTimeOffset Modified, long Bytes);

/// 캐시 정리 (D-62).
public static class CacheCleanup
{
    /// 캐시 폴더 이름으로 쓸 수 있는 item id (소문자 hex 32자). 정리는 이 이름의 폴더만 건드린다.
    public static bool IsCacheId(string name) => name.Length == 32 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// 지울 id 목록: 24h 넘은 것, 그다음 오래된 순으로 총량이 한도 이하가 될 때까지. protectedId(현재 클립보드 항목)는 지우지 않는다.
    public static List<string> Plan(IEnumerable<CacheEntry> entries, DateTimeOffset now, string? protectedId, TimeSpan? maxAge = null, long maxBytes = 200L << 20)
    {
        var age = maxAge ?? TimeSpan.FromHours(24);
        var candidates = entries.Where(e => IsCacheId(e.Id)).ToList();
        var delete = candidates.Where(e => e.Id != protectedId && now - e.Modified > age).Select(e => e.Id).ToHashSet();
        var remaining = candidates.Where(e => !delete.Contains(e.Id)).OrderBy(e => e.Modified).ToList();
        var total = remaining.Sum(e => e.Bytes);
        while (total > maxBytes && remaining.FindIndex(e => e.Id != protectedId) is var i and >= 0)
        {
            total -= remaining[i].Bytes;
            delete.Add(remaining[i].Id);
            remaining.RemoveAt(i);
        }
        return candidates.Select(e => e.Id).Where(delete.Contains).ToList();
    }
}

public sealed record CatchUpCandidate(long Seq, string DeviceId, bool Purged, IReadOnlyList<string> Kinds);
public sealed record CatchUpPlan(long NewLastSeq, CatchUpCandidate? Target);

/// 마지막 송·수신 항목 5개의 해시를 60초간 보관 (spec 6.4, 필수 방어).
public sealed class RecentHashRing(int capacity = 5, double ttlSeconds = 60)
{
    readonly List<(string Hash, DateTimeOffset At)> entries = [];

    public void Add(string hash, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        Prune(t);
        entries.RemoveAll(e => e.Hash == hash);
        entries.Add((hash, t));
        if (entries.Count > capacity) entries.RemoveRange(0, entries.Count - capacity);
    }

    public bool Contains(string hash, DateTimeOffset? now = null)
    {
        Prune(now ?? DateTimeOffset.UtcNow);
        return entries.Any(e => e.Hash == hash);
    }

    void Prune(DateTimeOffset now) => entries.RemoveAll(e => (now - e.At).TotalSeconds > ttlSeconds);
}

public static class ServerUrlInput
{
    /// https만 허용, 로컬 개발용 localhost/127.0.0.1의 http만 예외. 경로·쿼리 없음, 끝의 `/` 제거.
    public static Uri? Parse(string raw)
    {
        var s = raw.Trim().TrimEnd('/');
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return null;
        if (u.AbsolutePath != "/" || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return null;
        if (u.Scheme == Uri.UriSchemeHttps) return u;
        if (u.Scheme == Uri.UriSchemeHttp && (u.Host == "localhost" || u.Host == "127.0.0.1")) return u;
        return null;
    }
}
