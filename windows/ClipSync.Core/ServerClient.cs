using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace ClipSync.Core;

// 서버 API(spec 5장) 클라이언트. protocol/ref/src/peer.ts, mac ServerClient.swift 와 같은 동작.

public sealed class ServerItem
{
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("id")] public string Id { get; init; } = "";               // 소문자 hex (D-35)
    [JsonPropertyName("device_id")] public string DeviceId { get; init; } = "";
    [JsonPropertyName("created_at")] public ulong CreatedAt { get; init; }
    [JsonPropertyName("header")] public string Header { get; init; } = "";       // 표준 base64
    [JsonPropertyName("body_size")] public long BodySize { get; init; }
    /// WS `item` 이벤트에는 없다 (방금 커밋된 항목이므로 false).
    [JsonPropertyName("purged")] public bool Purged { get; init; }
}

public sealed record ConfigBlob(long Version, byte[] Blob);

public abstract record ServerEvent
{
    public sealed record Hello(long Seq, ConfigBlob? VaultConfig) : ServerEvent;
    public sealed record Item(ServerItem Value, byte[]? InlineBody) : ServerEvent;
    public sealed record BodyPurged(string Id) : ServerEvent;
    public sealed record Config(ConfigBlob Value) : ServerEvent;
    public sealed record Closed(string? Reason) : ServerEvent;
}

public sealed record ReceivedItem(long Seq, string Id, string DeviceId, JsonElement Header, List<BundleEntry>? Entries)
{
    public IReadOnlyList<string> Kinds =>
        Header.TryGetProperty("kinds", out var k) && k.ValueKind == JsonValueKind.Array
            ? k.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
    public string Preview => Header.TryGetProperty("preview", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
}

public sealed class ServerException(int status, string op) : Exception($"{op}: HTTP {status}")
{
    public int Status { get; } = status;
    public string Op { get; } = op;
}

/// 암호화까지 끝낸 업로드 단위. 재시도는 같은 PreparedItem을 다시 업로드한다 (PUT/POST 모두 idempotent).
public sealed record PreparedItem(string Id, byte[] SealedBody, byte[] SealedHeader, ulong CreatedAt);

public sealed class ServerClient : IDisposable
{
    public Uri BaseUrl { get; }
    public Keys Keys { get; }
    public string DeviceId { get; }
    public string AuthorizationHeader { get; }
    readonly HttpClient http;

    public ServerClient(Uri baseUrl, Keys keys, string? deviceId = null, HttpMessageHandler? handler = null)
    {
        BaseUrl = baseUrl;
        Keys = keys;
        DeviceId = deviceId ?? NewDeviceId();
        AuthorizationHeader = "Bearer " + Base64Url.EncodeToString(keys.AuthToken);
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromMinutes(5);   // 대용량 업로드/다운로드 (spec 5.2). 기본 100초는 짧다.
    }

    public void Dispose() => http.Dispose();

    /// UUID v4 16바이트를 소문자 hex로. Guid.ToByteArray()는 혼합 엔디언이라 쓰지 않는다 (D-33).
    public static string NewDeviceId()
    {
        var b = RandomNumberGenerator.GetBytes(16);
        b[6] = (byte)((b[6] & 0x0F) | 0x40);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return Protocol.UuidHex(b);
    }

    HttpRequestMessage Request(HttpMethod method, string path, byte[]? body = null, params (string, string)[] headers)
    {
        var r = new HttpRequestMessage(method, new Uri(BaseUrl.GetLeftPart(UriPartial.Authority) + path));
        r.Headers.TryAddWithoutValidation("Authorization", AuthorizationHeader);
        foreach (var (k, v) in headers) r.Headers.TryAddWithoutValidation(k, v);
        if (body is not null)
        {
            // ByteArrayContent는 Content-Length를 보낸다 (길이 없는 StreamContent는 chunked → 서버 411).
            r.Content = new ByteArrayContent(body);
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        return r;
    }

    static void Expect(HttpResponseMessage r, int status, string op)
    {
        if ((int)r.StatusCode != status) throw new ServerException((int)r.StatusCode, op);
    }

    // ---- 송신 ----

    public PreparedItem Prepare(IEnumerable<BundleEntry> entries, IEnumerable<string> kinds, string? previewText = null)
    {
        var id = RandomNumberGenerator.GetBytes(16);
        var dev = Convert.FromHexString(DeviceId);
        var createdAt = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var body = Protocol.EncodeBundle(entries);
        var header = new Dictionary<string, object> { ["v"] = 1, ["kinds"] = kinds.ToArray(), ["body_plain_size"] = body.Length };
        if (previewText is not null) header["preview"] = Protocol.PreviewOf(previewText);
        var headerJson = JsonSerializer.SerializeToUtf8Bytes(header);
        return new PreparedItem(
            Protocol.UuidHex(id),
            Protocol.Seal(Keys.EncKey, Protocol.ItemAad(id, dev, createdAt, Protocol.PartBody), body),
            Protocol.Seal(Keys.EncKey, Protocol.ItemAad(id, dev, createdAt, Protocol.PartHeader), headerJson),
            createdAt);
    }

    /// 2단계 업로드(PUT body → POST commit). 반환: 서버가 부여한 seq.
    public async Task<long> UploadAsync(PreparedItem item, CancellationToken ct = default)
    {
        using (var put = await http.SendAsync(Request(HttpMethod.Put, $"/v1/items/{item.Id}/body", item.SealedBody, ("X-Device-Id", DeviceId)), ct).ConfigureAwait(false))
            Expect(put, 204, "PUT body");
        using var post = await http.SendAsync(Request(HttpMethod.Post, "/v1/items", item.SealedHeader,
            ("X-Item-Id", item.Id), ("X-Device-Id", DeviceId), ("X-Created-At", item.CreatedAt.ToString())), ct).ConfigureAwait(false);
        Expect(post, 200, "POST commit");
        using var doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("seq").GetInt64();
    }

    public async Task<(string Id, long Seq)> SendAsync(IEnumerable<BundleEntry> entries, IEnumerable<string> kinds, string? previewText = null, CancellationToken ct = default)
    {
        var item = Prepare(entries, kinds, previewText);
        return (item.Id, await UploadAsync(item, ct).ConfigureAwait(false));
    }

    public Task<(string Id, long Seq)> SendTextAsync(string text, CancellationToken ct = default) =>
        SendAsync([new BundleEntry(1, "", Encoding.UTF8.GetBytes(text))], ["text"], text, ct);

    // ---- 조회 / 수신 ----

    public async Task<List<ServerItem>> ListAsync(long since = 0, CancellationToken ct = default)
    {
        using var r = await http.SendAsync(Request(HttpMethod.Get, $"/v1/items?since={since}"), ct).ConfigureAwait(false);
        Expect(r, 200, "list");
        return JsonSerializer.Deserialize<List<ServerItem>>(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) ?? [];
    }

    /// header를 복호화하고, withBody이면 본문도 받아 복호화한다. 본문이 삭제됐으면(404/410) Entries=null.
    public async Task<ReceivedItem> ReceiveAsync(ServerItem item, byte[]? inlineBody = null, bool withBody = true, CancellationToken ct = default)
    {
        var id = Convert.FromHexString(item.Id);
        var dev = Convert.FromHexString(item.DeviceId);
        var plain = Protocol.Open(Keys.EncKey, Protocol.ItemAad(id, dev, item.CreatedAt, Protocol.PartHeader), Convert.FromBase64String(item.Header));
        using var doc = JsonDocument.Parse(plain);
        var header = doc.RootElement.Clone();
        List<BundleEntry>? entries = null;
        if (withBody && !item.Purged)
        {
            var sealed_ = inlineBody;
            if (sealed_ is null)
            {
                using var r = await http.SendAsync(Request(HttpMethod.Get, $"/v1/items/{item.Id}/body"), ct).ConfigureAwait(false);
                var s = (int)r.StatusCode;
                if (s == 200) sealed_ = await r.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                else if (s != 404 && s != 410) throw new ServerException(s, "GET body");
            }
            if (sealed_ is not null)
                entries = Protocol.DecodeBundle(Protocol.Open(Keys.EncKey, Protocol.ItemAad(id, dev, item.CreatedAt, Protocol.PartBody), sealed_));
        }
        return new ReceivedItem(item.Seq, item.Id, item.DeviceId, header, entries);
    }

    /// 수신 후 삭제 (D-16).
    public async Task DeleteBodyAsync(string id, CancellationToken ct = default)
    {
        using var r = await http.SendAsync(Request(HttpMethod.Delete, $"/v1/items/{id}/body"), ct).ConfigureAwait(false);
        Expect(r, 204, "DELETE body");
    }

    public async Task<ConfigBlob?> GetConfigAsync(CancellationToken ct = default)
    {
        using var r = await http.SendAsync(Request(HttpMethod.Get, "/v1/config"), ct).ConfigureAwait(false);
        if ((int)r.StatusCode == 404) return null;
        Expect(r, 200, "GET config");
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return ParseConfig(doc.RootElement) ?? throw new ServerException(200, "GET config (bad body)");
    }

    static ConfigBlob? ParseConfig(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("version", out var v) || !e.TryGetProperty("blob", out var b)) return null;
        try { return new ConfigBlob(v.GetInt64(), Convert.FromBase64String(b.GetString() ?? "")); }
        catch (FormatException) { return null; }
    }

    // ---- WebSocket ----

    /// 서버 이벤트 스트림. 텍스트 "ping"을 pingInterval(기본 30초)마다 보내고, pongTimeout(기본 90초) 동안 아무 메시지도 없으면
    /// 끊고 Closed를 내보낸다 (D-28). ClientWebSocket.KeepAliveInterval의 제어 프레임은 DO auto-response와 맞지 않으므로 끈다.
    /// 마지막 이벤트는 항상 Closed. 재연결/백오프는 호출자 책임. pingPayload는 타임아웃 경로 테스트용.
    public async IAsyncEnumerable<ServerEvent> EventsAsync(TimeSpan? pingInterval = null, TimeSpan? pongTimeout = null,
        string pingPayload = "ping", [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<ServerEvent>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = RunSocketAsync(channel.Writer, pingInterval ?? TimeSpan.FromSeconds(30), pongTimeout ?? TimeSpan.FromSeconds(90), pingPayload, cts.Token);
        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                yield return ev;
        }
        finally
        {
            cts.Cancel();
            try { await run.ConfigureAwait(false); } catch { /* 종료 중 오류는 무시 */ }
        }
    }

    async Task RunSocketAsync(ChannelWriter<ServerEvent> writer, TimeSpan pingInterval, TimeSpan pongTimeout, string pingPayload, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.Zero;
        ws.Options.SetRequestHeader("Authorization", AuthorizationHeader);
        var scheme = BaseUrl.Scheme == "https" ? "wss" : "ws";
        var uri = new Uri($"{scheme}://{BaseUrl.Authority}/v1/ws?device_id={DeviceId}");
        string? reason = null;
        using var inner = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await ws.ConnectAsync(uri, inner.Token).ConfigureAwait(false);
            var last = Environment.TickCount64;

            async Task ReceiveLoop()
            {
                var buf = new byte[16 * 1024];
                using var msg = new MemoryStream();
                while (!inner.Token.IsCancellationRequested)
                {
                    msg.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(buf, inner.Token).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) { reason = $"closed by server ({ws.CloseStatus})"; return; }
                        msg.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);   // 프레임을 EndOfMessage까지 모은다
                    Interlocked.Exchange(ref last, Environment.TickCount64);
                    if (r.MessageType != WebSocketMessageType.Text) continue;
                    var text = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
                    if (text == "pong") continue;
                    if (ParseEvent(text) is { } ev) await writer.WriteAsync(ev, inner.Token).ConfigureAwait(false);
                }
            }

            async Task PingLoop()
            {
                var payload = Encoding.UTF8.GetBytes(pingPayload);
                while (!inner.Token.IsCancellationRequested)
                {
                    await Task.Delay(pingInterval, inner.Token).ConfigureAwait(false);
                    if (Environment.TickCount64 - Interlocked.Read(ref last) > (long)pongTimeout.TotalMilliseconds)
                    {
                        reason = $"no pong for {pongTimeout.TotalSeconds:0}s";
                        return;
                    }
                    await ws.SendAsync(payload, WebSocketMessageType.Text, true, inner.Token).ConfigureAwait(false);
                }
            }

            var recv = ReceiveLoop();
            var ping = PingLoop();
            var first = await Task.WhenAny(recv, ping).ConfigureAwait(false);
            if (first.IsFaulted) reason ??= first.Exception!.GetBaseException().Message;
            inner.Cancel();
            try { await Task.WhenAll(recv, ping).ConfigureAwait(false); } catch { /* 취소 */ }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            reason ??= e.Message;
        }
        catch (OperationCanceledException)
        {
            reason ??= "cancelled";
        }
        finally
        {
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, t.Token).ConfigureAwait(false);
                }
                catch { /* 이미 끊김 */ }
            }
            writer.TryWrite(new ServerEvent.Closed(reason));
            writer.TryComplete();
        }
    }

    public static ServerEvent? ParseEvent(string s)
    {
        try
        {
            using var doc = JsonDocument.Parse(s);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("t", out var t)) return null;
            switch (t.GetString())
            {
                case "hello":
                    return new ServerEvent.Hello(o.GetProperty("seq").GetInt64(),
                        o.TryGetProperty("config", out var c) ? ParseConfig(c) : null);
                case "item":
                    var item = o.Deserialize<ServerItem>()!;
                    byte[]? inline = o.TryGetProperty("inline_body", out var ib) && ib.ValueKind == JsonValueKind.String
                        ? Convert.FromBase64String(ib.GetString()!) : null;
                    return new ServerEvent.Item(item, inline);
                case "body_purged":
                    return o.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? new ServerEvent.BodyPurged(id.GetString()!) : null;
                case "config":
                    return ParseConfig(o) is { } cfg ? new ServerEvent.Config(cfg) : null;
                default:
                    return null;
            }
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
