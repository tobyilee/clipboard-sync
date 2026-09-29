using System.Text;
using System.Threading.Channels;
using ClipSync.Core;

namespace ClipSync.App;

/// 감시 → 송신, WebSocket → 수신 적용 (spec 6.2~6.5). mac SyncEngine.swift 와 같은 규칙.
/// 모든 메서드는 UI(STA) 스레드에서 호출되고, await 후에도 UI 스레드로 돌아온다 (Core는 ConfigureAwait(false)).
/// 로컬 우선·pending·이미지/파일·수신 후 삭제는 M5/M6.
sealed class SyncEngine : IDisposable
{
    public enum Connection { Off, Connecting, Connected, Offline }

    public sealed record RecentItem(long Seq, string Id, string DeviceId, IReadOnlyList<string> Kinds, string Preview, ulong CreatedAt, bool Purged);

    readonly Settings settings;
    readonly ClipboardListener listener = new();
    readonly System.Windows.Forms.Timer debounce = new() { Interval = 100 };   // spec 8: ~100ms debounce
    readonly RecentHashRing ring = new();
    static readonly HashSet<string> AllowedKinds = ["text", "html"];   // config 없음 = 텍스트만 (fail-closed, 4.4)

    ServerClient? client;
    CancellationTokenSource? cts;
    CancellationTokenSource? applyCts;
    Channel<(PreparedItem Item, string[] Kinds, string Preview)>? uploads;
    uint lastWrittenSeq;

    public Connection State { get; private set; } = Connection.Off;
    public List<RecentItem> Recent { get; } = [];
    public string? LastMessage { get; private set; }
    public event Action? Changed;
    public event Action<string>? Alert;   // 사용자 조치가 필요한 알림 (D-45: balloon)

    public SyncEngine(Settings settings)
    {
        this.settings = settings;
        listener.Changed += () => { debounce.Stop(); debounce.Start(); };
        debounce.Tick += (_, _) => { debounce.Stop(); OnClipboardChanged(); };
    }

    bool CanApply => settings.SyncEnabled && settings.ReceiveEnabled && !settings.IsPaused;
    bool CanSend => settings.SyncEnabled && settings.SendEnabled && !settings.IsPaused;

    void Notify() => Changed?.Invoke();

    // ---- 시작/중지 ----

    public void Start()
    {
        Stop();
        if (!settings.SyncEnabled || settings.ServerUri is not { } url) return;
        string? pass;
        try { pass = CredentialStore.Load(); } catch (Exception e) { Log.Error("credential load: " + e.Message); return; }
        if (pass is null) return;
        client = new ServerClient(url, Protocol.DeriveKeys(pass), settings.DeviceId);
        lastWrittenSeq = ClipboardIO.SequenceNumber;   // 시작 시점의 클립보드는 보내지 않는다
        cts = new CancellationTokenSource();
        uploads = Channel.CreateUnbounded<(PreparedItem, string[], string)>();
        _ = RunLoop(client, cts.Token);
        _ = UploadLoop(client, uploads.Reader, cts.Token);
        Log.Info($"engine start device={settings.DeviceId} server={url.Host}");
    }

    public void Stop()
    {
        cts?.Cancel(); cts = null;
        applyCts?.Cancel(); applyCts = null;
        uploads?.Writer.TryComplete(); uploads = null;
        client?.Dispose(); client = null;
        State = Connection.Off;
        Notify();
    }

    public void Dispose()
    {
        Stop();
        debounce.Dispose();
        listener.Dispose();
    }

    // ---- 연결 / 수신 ----

    async Task RunLoop(ServerClient c, CancellationToken ct)
    {
        try { await RunLoopInner(c, ct); }
        catch (Exception) when (ct.IsCancellationRequested) { }   // Stop() 중 client가 해제되며 나는 예외는 무시
    }

    async Task RunLoopInner(ServerClient c, CancellationToken ct)
    {
        var backoff = 1.0;
        while (!ct.IsCancellationRequested)
        {
            State = Connection.Connecting; Notify();
            try
            {
                await foreach (var ev in c.EventsAsync(ct: ct))
                {
                    if (ct.IsCancellationRequested) return;
                    switch (ev)
                    {
                        case ServerEvent.Hello h:
                            State = Connection.Connected; LastMessage = null; backoff = 1; Notify();
                            Log.Info($"connected hello.seq={h.Seq}");
                            await CatchUp(c, h.Seq, ct);
                            break;
                        case ServerEvent.Item it:
                            await Handle(c, [it.Value], new() { [it.Value.Id] = it.InlineBody }, true, ct);
                            break;
                        case ServerEvent.BodyPurged p:
                            var i = Recent.FindIndex(r => r.Id == p.Id);
                            if (i >= 0) { Recent[i] = Recent[i] with { Purged = true }; Notify(); }
                            break;
                        case ServerEvent.Config:
                            break;   // vault 설정은 M5
                        case ServerEvent.Closed cl:
                            if (!ct.IsCancellationRequested) Log.Info("disconnected: " + cl.Reason);
                            break;
                    }
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { Log.Error("event loop: " + e.Message); }
            if (ct.IsCancellationRequested) return;
            State = Connection.Offline; Notify();
            try { await Task.Delay(TimeSpan.FromSeconds(backoff * (0.8 + Random.Shared.NextDouble() * 0.4)), ct); }
            catch (OperationCanceledException) { return; }
            backoff = Math.Min(60, backoff * 2);
        }
    }

    /// 첫 실행이면 last_seq=hello.seq(과거 항목 자동 적용 방지), 아니면 since=last_seq로 따라잡는다.
    /// 이 동안 도착한 WS 이벤트는 스트림에 쌓였다가 이후 순서대로 처리된다 (D-42).
    async Task CatchUp(ServerClient c, long helloSeq, CancellationToken ct)
    {
        try
        {
            if (settings.LastSeq is not { } last)
            {
                settings.LastSeq = helloSeq; settings.Save();
                await Handle(c, await c.ListAsync(0, ct), [], false, ct);
                return;
            }
            await Handle(c, await c.ListAsync(last, ct), [], true, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            LastMessage = "목록 조회 실패: " + e.Message; Log.Error("catch-up: " + e.Message); Notify();
        }
    }

    async Task Handle(ServerClient c, List<ServerItem> items, Dictionary<string, byte[]?> inline, bool apply, CancellationToken ct)
    {
        if (settings.LastSeq is not { } last) return;
        var candidates = new List<CatchUpCandidate>();
        foreach (var it in items)
        {
            ReceivedItem r;
            try { r = await c.ReceiveAsync(it, withBody: false, ct: ct); }
            catch (ProtocolException) { Log.Error($"header decrypt failed id={it.Id}"); continue; }   // 폐기 (6.3-5)
            candidates.Add(new CatchUpCandidate(it.Seq, it.DeviceId, it.Purged, r.Kinds));
            AddRecent(new RecentItem(it.Seq, it.Id, it.DeviceId, r.Kinds, r.Preview, it.CreatedAt, it.Purged));
        }
        var plan = SyncLogic.PlanCatchUp(candidates, last, settings.DeviceId, apply && CanApply, AllowedKinds);
        settings.LastSeq = plan.NewLastSeq; settings.Save();
        Notify();
        if (plan.Target is { } t && items.FirstOrDefault(i => i.Seq == t.Seq) is { } target)
        {
            applyCts?.Cancel();   // 더 새 항목이 오면 진행 중인 이전 다운로드는 취소 (6.3-2)
            applyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Apply(c, target, inline.GetValueOrDefault(target.Id), applyCts.Token);
        }
    }

    void AddRecent(RecentItem item)
    {
        if (Recent.Any(r => r.Id == item.Id)) return;
        Recent.Add(item);
        Recent.Sort((a, b) => b.Seq.CompareTo(a.Seq));
        if (Recent.Count > 20) Recent.RemoveRange(20, Recent.Count - 20);
    }

    /// 원격 항목을 클립보드에 적용한다. 텍스트 항목은 서버 본문을 지우지 않는다 (6.3-3).
    async Task Apply(ServerClient c, ServerItem it, byte[]? inline, CancellationToken ct)
    {
        try
        {
            var r = await c.ReceiveAsync(it, inline, true, ct);
            if (ct.IsCancellationRequested) return;
            if (r.Entries is null) { LastMessage = "서버에서 삭제된 항목입니다"; Notify(); return; }
            var plain = r.Entries.FirstOrDefault(e => e.Type == 1) is { } p ? Encoding.UTF8.GetString(p.Data) : null;
            var html = r.Entries.FirstOrDefault(e => e.Type == 2) is { } h ? Encoding.UTF8.GetString(h.Data) : null;
            var hash = SyncLogic.ContentHash(plain, html);
            if (hash is not null) ring.Add(hash);   // 되돌아오는 변경을 업로드하지 않도록 (D-24)
            if (hash is not null && hash == ClipboardIO.CurrentHash(listener.Handle))
            {
                Log.Info($"apply skipped (same content) id={it.Id} seq={it.Seq}");   // D-49
            }
            else
            {
                lastWrittenSeq = ClipboardIO.Write(listener.Handle, r.Entries, it.Id);
                Log.Info($"applied id={it.Id} seq={it.Seq} kinds={string.Join(',', r.Kinds)}");
            }
            settings.LastAppliedSeq = Math.Max(settings.LastAppliedSeq ?? 0, it.Seq); settings.Save();
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            LastMessage = "적용 실패: " + e.Message; Log.Error($"apply id={it.Id}: {e.Message}"); Notify();
        }
    }

    /// 최근 항목 메뉴에서 선택: 서버에 본문이 남아 있으면 받아서 복원한다.
    public async void Restore(RecentItem item)
    {
        if (client is not { } c || cts is not { } token) return;
        try
        {
            var it = (await c.ListAsync(Math.Max(0, item.Seq - 1), token.Token)).FirstOrDefault(i => i.Id == item.Id);
            if (it is null) { LastMessage = "서버에서 삭제됨"; Notify(); return; }
            await Apply(c, it, null, token.Token);
        }
        catch (Exception e) { Log.Error("restore: " + e.Message); }
    }

    // ---- 감시 / 송신 ----

    void OnClipboardChanged()
    {
        var seq = ClipboardIO.SequenceNumber;
        if (seq == lastWrittenSeq) return;   // 우리가 쓴 변경 (D-44)
        if (!CanSend || client is not { } c || uploads is not { } q) return;   // 꺼져 있으면 보내지 않고 pending도 만들지 않는다 (6.2-3)

        var read = ClipboardIO.Read(listener.Handle);
        if (read is ClipboardIO.ReadResult.Busy) { Log.Error("clipboard busy (OpenClipboard failed)"); return; }
        if (read is not ClipboardIO.ReadResult.Text { Value: var content }) return;
        if (SyncLogic.ContentHash(content.Plain, content.Html) is not { } hash || ring.Contains(hash)) return;
        ring.Add(hash);

        var entries = new List<BundleEntry>();
        var kinds = new List<string>();
        if (content.Plain is { } plain) { entries.Add(new BundleEntry(1, "", Encoding.UTF8.GetBytes(plain))); kinds.Add("text"); }
        if (content.Html is { } html) { entries.Add(new BundleEntry(2, "", Encoding.UTF8.GetBytes(html))); kinds.Add("html"); }
        if (entries.Sum(e => e.Data.Length) > 1 << 20)
        {
            LastMessage = "1 MiB를 넘는 텍스트는 보내지 않습니다"; Alert?.Invoke(LastMessage); Notify(); return;
        }
        var item = c.Prepare(entries, kinds, content.Plain);
        q.Writer.TryWrite((item, kinds.ToArray(), Protocol.PreviewOf(content.Plain ?? "")));
    }

    /// 순서대로 한 개씩. 지수 백오프로 최대 3회, 같은 PreparedItem을 재전송하므로 중복 항목이 생기지 않는다 (idempotent).
    async Task UploadLoop(ServerClient c, ChannelReader<(PreparedItem Item, string[] Kinds, string Preview)> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var (item, kinds, preview) in reader.ReadAllAsync(ct))
            {
                var delay = 1.0;
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        var seq = await c.UploadAsync(item, ct);
                        Log.Info($"sent id={item.Id} seq={seq} bytes={item.SealedBody.Length}");
                        // 서버는 업로더의 소켓에 이벤트를 보내지 않으므로 내가 보낸 항목을 최근 항목에 직접 추가한다.
                        AddRecent(new RecentItem(seq, item.Id, settings.DeviceId, kinds, preview, item.CreatedAt, false));
                        LastMessage = null; Notify();
                        break;
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        Log.Error($"upload id={item.Id} attempt={attempt}: {e.Message}");
                        if (attempt == 3)
                        {
                            LastMessage = "전송 실패: " + e.Message; Alert?.Invoke(LastMessage); Notify();
                            break;
                        }
                        await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                        delay *= 2;
                    }
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }   // Stop() 중 취소·해제 예외는 무시
    }
}
