using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClipSync.Core;
using Xunit;

namespace ClipSync.Core.Tests;

// 서버 상호운용 테스트. CLIPSYNC_URL / CLIPSYNC_PASSPHRASE 가 있을 때만 실행한다 (없으면 통과 처리).
// CLI 교차 테스트는 node 와 protocol/ref 가 있어야 한다 (Mac 개발 환경).
public class ServerInteropTests
{
    const string VectorPass = "abacus abdomen abide abnormal abrasion abroad absence";
    static readonly string? Url = Environment.GetEnvironmentVariable("CLIPSYNC_URL");
    static readonly string? Pass = Environment.GetEnvironmentVariable("CLIPSYNC_PASSPHRASE");
    static bool Enabled => Url is not null && Pass is not null;
    static readonly Lazy<Keys> K = new(() => Protocol.DeriveKeys(Pass!));
    static ServerClient Client() => new(new Uri(Url!), K.Value);

    static string RefDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "protocol", "ref", "src", "cli.ts"))) d = d.Parent;
        return d == null ? throw new DirectoryNotFoundException("protocol/ref") : Path.Combine(d.FullName, "protocol", "ref");
    }

    static string RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("node") { WorkingDirectory = RefDir(), RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("src/cli.ts");
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["CLIPSYNC_URL"] = Url;
        psi.Environment["CLIPSYNC_PASSPHRASE"] = Pass;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    [Fact]
    public void BearerMatchesNodeBase64Url()
    {
        using var c = new ServerClient(new Uri("https://x.test"), Protocol.DeriveKeys(VectorPass));
        Assert.Equal("Bearer uNwMUkQX9SphZQHLHXVX8C9ios7PNtnbUYYnrsRk_Ew", c.AuthorizationHeader);
    }

    [Fact]
    public void DeviceIdIsLowercaseHexV4()
    {
        var id = ServerClient.NewDeviceId();
        Assert.Matches("^[0-9a-f]{12}4[0-9a-f]{3}[89ab][0-9a-f]{15}$", id);
    }

    [Fact]
    public void ParseEventCases()
    {
        Assert.Equal(new ServerEvent.Hello(7, null), ServerClient.ParseEvent("""{"t":"hello","seq":7,"config":null}"""));
        var item = Assert.IsType<ServerEvent.Item>(ServerClient.ParseEvent(
            """{"t":"item","seq":3,"id":"aa","device_id":"bb","created_at":5,"header":"AQID","body_size":9,"inline_body":null}"""));
        Assert.Equal(3, item.Value.Seq);
        Assert.False(item.Value.Purged);   // WS item에는 purged 키가 없다
        Assert.Null(item.InlineBody);
        Assert.Equal(new ServerEvent.BodyPurged("ab"), ServerClient.ParseEvent("""{"t":"body_purged","id":"ab"}"""));
        var cfg = Assert.IsType<ServerEvent.Config>(ServerClient.ParseEvent("""{"t":"config","version":2,"blob":"AQI="}"""));
        Assert.Equal([1, 2], cfg.Value.Blob);
        Assert.Null(ServerClient.ParseEvent("""{"t":"unknown"}"""));
        Assert.Null(ServerClient.ParseEvent("not json"));
    }

    [Fact]
    public async Task CSharpSendsCliDecrypts()
    {
        if (!Enabled) return;
        using var c = Client();
        var text = $"csharp→cli 한글 🎉 {Guid.NewGuid()}";
        var sent = await c.SendTextAsync(text);
        var output = RunCli("list");
        Assert.Contains(sent.Id, output);
        Assert.Contains(text, output);
    }

    [Fact]
    public async Task CliSendsCSharpDecrypts()
    {
        if (!Enabled) return;
        var text = $"cli→csharp 한글 🎉 {Guid.NewGuid()}";
        var id = JsonDocument.Parse(RunCli("send-html", "<p>" + text + "</p>", text)).RootElement.GetProperty("id").GetString()!;
        using var c = Client();
        var item = (await c.ListAsync()).Single(i => i.Id == id);
        var r = await c.ReceiveAsync(item);
        Assert.Equal(["text", "html"], r.Kinds);
        Assert.Equal(text, Encoding.UTF8.GetString(r.Entries!.Single(e => e.Type == 1).Data));
        Assert.Equal("<p>" + text + "</p>", Encoding.UTF8.GetString(r.Entries!.Single(e => e.Type == 2).Data));
    }

    [Fact]
    public async Task WebSocketDeliversInlineThenDeleteIsPurged()
    {
        if (!Enabled) return;
        using var receiver = Client();
        using var sender = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var e = receiver.EventsAsync(ct: cts.Token).GetAsyncEnumerator();
        Assert.True(await e.MoveNextAsync());
        Assert.IsType<ServerEvent.Hello>(e.Current);
        var text = $"ws {Guid.NewGuid()}";
        var sent = await sender.SendTextAsync(text);
        ServerEvent.Item? got = null;
        while (await e.MoveNextAsync())
            if (e.Current is ServerEvent.Item it && it.Value.Id == sent.Id) { got = it; break; }
        Assert.NotNull(got);
        Assert.NotNull(got!.InlineBody);
        var r = await receiver.ReceiveAsync(got.Value, got.InlineBody);
        Assert.Equal(text, Encoding.UTF8.GetString(r.Entries![0].Data));
        await receiver.DeleteBodyAsync(sent.Id);
        var purged = false;
        while (await e.MoveNextAsync())
            if (e.Current is ServerEvent.BodyPurged p && p.Id == sent.Id) { purged = true; break; }
        Assert.True(purged);
        await e.DisposeAsync();
    }

    /// D-28: 서버는 "ping"이 아닌 텍스트에 응답하지 않으므로 "xping"이면 pong 끊김을 재현한다.
    [Fact]
    public async Task SilentConnectionIsClosedAfterPongTimeout()
    {
        if (!Enabled) return;
        using var c = Client();
        var sw = Stopwatch.StartNew();
        ServerEvent? last = null;
        await foreach (var ev in c.EventsAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "xping")) last = ev;
        var closed = Assert.IsType<ServerEvent.Closed>(last);
        Assert.Contains("no pong", closed.Reason);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    /// 대조군: 올바른 "ping"이면 같은 짧은 타임아웃에서도 연결이 유지된다.
    [Fact]
    public async Task ProperPingKeepsConnectionAlive()
    {
        if (!Enabled) return;
        using var c = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var closedEarly = false;
        await foreach (var ev in c.EventsAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), ct: cts.Token))
            if (ev is ServerEvent.Closed && !cts.IsCancellationRequested) closedEarly = true;
        Assert.False(closedEarly);
    }

    [Fact]
    public async Task WrongPassphraseIs401()
    {
        if (!Enabled) return;
        using var bad = new ServerClient(new Uri(Url!), Protocol.DeriveKeys("wrong passphrase not registered"));
        var ex = await Assert.ThrowsAsync<ServerException>(() => bad.ListAsync());
        Assert.Equal(401, ex.Status);
    }
}
