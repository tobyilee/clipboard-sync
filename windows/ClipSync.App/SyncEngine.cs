using System.Text;
using ClipSync.Core;

namespace ClipSync.App;

/// 감시 → 송신, WebSocket → 수신 적용 (spec 6.2~6.5), vault 설정(4.4, D-50; Windows는 읽기 전용),
/// 수신 후 삭제·캐시(D-61, D-62), 로컬 우선·pending·재개 catch-up(D-58~D-60). mac SyncEngine.swift 와 같은 규칙.
/// 모든 메서드는 UI(STA) 스레드에서 호출되고, await 후에도 UI 스레드로 돌아온다 (Core는 ConfigureAwait(false)).
sealed class SyncEngine : IDisposable
{
    public enum Connection { Off, Connecting, Connected, Offline }

    public sealed record RecentItem(long Seq, string Id, string DeviceId, IReadOnlyList<string> Kinds, string Preview, ulong CreatedAt, bool Purged);

    /// 송신 큐의 한 단위. Observed는 이 내용을 읽었을 때의 클립보드 시퀀스 번호 (D-58).
    sealed record Outgoing(PreparedItem Item, string[] Kinds, string Preview, uint Observed);

    readonly Settings settings;
    readonly ClipboardListener listener = new();
    readonly System.Windows.Forms.Timer debounce = new() { Interval = 100 };   // spec 8: ~100ms debounce
    readonly System.Windows.Forms.Timer tick = new() { Interval = 1000 };       // 일시정지 만료 감지(D-60), 캐시 정리 주기
    readonly RecentHashRing ring = new();

    ServerClient? client;
    CancellationTokenSource? cts;
    CancellationTokenSource? applyCts;
    Task sendChain = Task.CompletedTask;
    uint lastWrittenSeq;
    Outgoing? pending;   // D-59: 전송 계층 실패로 못 보낸 가장 최근 항목 하나 (메모리만)
    bool outageAlerted;
    bool wasApplyEnabled;
    DateTimeOffset nextCleanup;

    public Connection State { get; private set; } = Connection.Off;
    public List<RecentItem> Recent { get; } = [];
    public string? LastMessage { get; private set; }
    /// "보내는 중…"/"받는 중…" (D-53)
    public string? Busy { get; private set; }
    public bool HasPending => pending is not null;
    public VaultConfig Config => settings.VaultConfig;
    public event Action? Changed;
    public event Action<string>? Alert;   // 사용자 조치가 필요한 알림 (D-45: balloon)

    public static string CacheRoot => Path.Combine(AppPaths.Root, "incoming");
    static string CacheDir(string id) => Path.Combine(CacheRoot, id);
    public bool HasCache(string id) => CacheCleanup.IsCacheId(id) && Directory.Exists(CacheDir(id));

    public SyncEngine(Settings settings)
    {
        this.settings = settings;
        listener.Changed += () => { debounce.Stop(); debounce.Start(); };
        debounce.Tick += (_, _) => { debounce.Stop(); OnClipboardChanged(); };
        tick.Tick += (_, _) => OnTick();
    }

    bool CanApply => settings.SyncEnabled && settings.ReceiveEnabled && !settings.IsPaused;
    bool CanSend => settings.SyncEnabled && settings.SendEnabled && !settings.IsPaused;

    void Notify() => Changed?.Invoke();

    void SetMessage(string? m, bool alert = false)
    {
        LastMessage = m;
        if (alert && m is not null) Alert?.Invoke(m);
        Notify();
    }

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
        wasApplyEnabled = CanApply;
        nextCleanup = DateTimeOffset.Now;   // 시작 직후 한 번
        tick.Start();
        _ = RunLoop(client, cts.Token);
        Log.Info($"engine start device={settings.DeviceId} server={url.Host} config.v={settings.ConfigVersion}");
    }

    public void Stop()
    {
        tick.Stop();
        cts?.Cancel(); cts = null;
        applyCts?.Cancel(); applyCts = null;
        client?.Dispose(); client = null;
        State = Connection.Off;
        Notify();
    }

    public void Dispose()
    {
        Stop();
        debounce.Dispose();
        tick.Dispose();
        listener.Dispose();
    }

    void OnTick()
    {
        var applyNow = CanApply;
        if (applyNow && !wasApplyEnabled) ResumeIfNeeded();   // 일시정지 만료·해제, 받기 on (D-60)
        wasApplyEnabled = applyNow;
        if (DateTimeOffset.Now >= nextCleanup)
        {
            nextCleanup = DateTimeOffset.Now.AddHours(1);
            _ = CleanupCache();
        }
    }

    // ---- 로컬 우선 (D-58) ----

    /// 우리가 쓰거나 보낸 내용이 지금 클립보드에 있다고 기록한다.
    void MarkOwn(uint seq)
    {
        // 늦게 끝난 옛 업로드가 더 새 기록을 덮지 않게 한다. 저장값이 지금 번호보다 크면 로그온 등으로 초기화된 것이므로 받아들인다.
        if (settings.OwnClipboardSeq is { } own && own <= ClipboardIO.SequenceNumber && seq < own) return;
        settings.OwnClipboardSeq = seq;
        settings.Save();
    }

    /// 마지막으로 쓰거나 보낸 뒤 사용자가 새로 복사했는가. 비어 있으면 아니다(일반 규칙으로 적용).
    bool LocalIsNewer()
    {
        if (Native.CountClipboardFormats() == 0) return false;
        if (ClipboardIO.HasOwnMarker(listener.Handle)) return false;
        return settings.OwnClipboardSeq is not { } own || ClipboardIO.SequenceNumber != own;
    }

    // ---- vault 설정 (D-50) ----

    /// 서버가 준 설정을 수용한다: 복호화 실패·version 이하이면 무시하고 캐시 유지.
    void Accept(ConfigBlob blob, ServerClient c)
    {
        if (blob.Version <= settings.ConfigVersion) return;
        VaultConfig cfg;
        try { cfg = VaultConfig.Open(blob, c.Keys.EncKey); }
        catch (ProtocolException) { Log.Error($"config v{blob.Version} decrypt failed — ignored"); return; }
        settings.ConfigVersion = blob.Version;
        settings.VaultConfig = cfg;
        settings.Save();
        Log.Info($"config v{blob.Version} images={cfg.Images} files={cfg.Files} max={cfg.MaxMediaBytes >> 20}MiB");
        Notify();
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
                            State = Connection.Connected; LastMessage = null; backoff = 1; outageAlerted = false; Notify();
                            Log.Info($"connected hello.seq={h.Seq}");
                            if (h.VaultConfig is { } cfg) Accept(cfg, c);
                            await CatchUpOnConnect(c, h.Seq, ct);
                            break;
                        case ServerEvent.Item it:
                            Log.Info($"event item seq={it.Value.Seq} id={it.Value.Id} from={it.Value.DeviceId[..8]} inline={it.InlineBody is not null} bytes={it.Value.BodySize}");
                            await Handle(c, [it.Value], new() { [it.Value.Id] = it.InlineBody }, ct);
                            break;
                        case ServerEvent.BodyPurged p:
                            var i = Recent.FindIndex(r => r.Id == p.Id);
                            if (i >= 0) { Recent[i] = Recent[i] with { Purged = true }; Notify(); }
                            break;
                        case ServerEvent.Config cf:
                            Accept(cf.Value, c);
                            break;
                        case ServerEvent.Closed cl:
                            if (!ct.IsCancellationRequested) Log.Info("disconnected: " + cl.Reason);
                            break;
                    }
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { Log.Error("event loop: " + e.Message); }
            if (ct.IsCancellationRequested) return;
            State = Connection.Offline; Notify();
            await Task.Delay(TimeSpan.FromSeconds(backoff * (0.8 + Random.Shared.NextDouble() * 0.4)), ct);
            backoff = Math.Min(60, backoff * 2);
        }
    }

    /// 재접속/첫 접속 직후 (D-42, D-59, D-60): 첫 실행이면 last_seq=hello.seq (과거 항목 자동 적용 방지).
    /// 아니면 pending을 먼저 올리고, 적용 워터마크(없으면 last_seq)부터 따라잡는다.
    async Task CatchUpOnConnect(ServerClient c, long helloSeq, CancellationToken ct)
    {
        if (settings.LastSeq is not { } last)
        {
            settings.LastSeq = helloSeq; settings.ApplyWatermark = null; settings.Save();
            try { await Handle(c, await c.ListAsync(0, ct), [], ct, apply: false); }
            catch (Exception e) when (!ct.IsCancellationRequested) { Log.Error("first list: " + e.Message); }
            return;
        }
        await CatchUp(c, Math.Min(last, settings.ApplyWatermark ?? last), ct);
    }

    async Task CatchUp(ServerClient c, long since, CancellationToken ct)
    {
        var hadPending = await FlushPending();
        try
        {
            // 서버는 최대 20개만 보관하므로 전부 받아 최근 항목을 채우고(재시작 뒤에도 목록 유지), 적용 판단은 since 이후만 본다.
            var items = await c.ListAsync(0, ct);
            var hold = hadPending || LocalIsNewer();
            await Handle(c, items, [], ct, since: since, hold: hold);
            if (CanApply) { settings.ApplyWatermark = null; settings.Save(); }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            SetMessage("목록 조회 실패: " + e.Message);
            Log.Error("catch-up: " + e.Message);
        }
    }

    /// 적용이 다시 켜졌을 때 꺼져 있던 동안의 항목을 로컬 우선 규칙으로 판단한다 (D-60).
    void ResumeIfNeeded()
    {
        if (settings.ApplyWatermark is not { } wm || client is not { } c || cts is not { } t || State != Connection.Connected) return;
        Log.Info($"apply resumed — catch-up since {wm}");
        _ = CatchUp(c, wm, t.Token);
    }

    /// since: 새 항목으로 볼 하한 (기본 last_seq). hold: 로컬 우선·pending 때문에 이번에는 적용하지 않음.
    async Task Handle(ServerClient c, List<ServerItem> items, Dictionary<string, byte[]?> inline, CancellationToken ct,
                      long? since = null, bool apply = true, bool hold = false)
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
        if (apply && !CanApply && settings.ApplyWatermark is null && candidates.Any(x => x.Seq > last))
            settings.ApplyWatermark = last;   // D-60: 적용이 꺼진 동안 도착한 항목을 재개 시 다시 본다
        var plan = SyncLogic.PlanCatchUp(candidates, since ?? last, settings.DeviceId, apply && CanApply, Config.AllowedKinds, hold);
        settings.LastSeq = Math.Max(last, plan.NewLastSeq); settings.Save();
        Notify();
        Log.Info($"handled {items.Count} item(s) lastSeq={settings.LastSeq} target={plan.Target?.Seq.ToString() ?? "-"}{(hold ? " (held: local newer or pending)" : "")}");
        if (plan.Target is { } t && items.FirstOrDefault(i => i.Seq == t.Seq) is { } target)
        {
            // 적용(대용량 다운로드 포함)은 별도 작업으로: 이벤트 처리를 막지 않고, 더 새 항목이 오면 이전 것을 취소한다 (6.3-2).
            applyCts?.Cancel();
            applyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Apply(c, target, inline.GetValueOrDefault(target.Id), applyCts.Token);
        }
    }

    void AddRecent(RecentItem item)
    {
        if (Recent.Any(r => r.Id == item.Id)) return;
        Recent.Add(item);
        Recent.Sort((a, b) => b.Seq.CompareTo(a.Seq));
        if (Recent.Count > 20) Recent.RemoveRange(20, Recent.Count - 20);
    }

    /// 원격 항목을 받아 클립보드에 적용하고, 이미지/파일이면 캐시한 뒤 서버 본문을 지운다 (D-61, D-62).
    async Task Apply(ServerClient c, ServerItem it, byte[]? inline, CancellationToken ct)
    {
        var large = inline is null && it.BodySize > 1 << 20;
        if (large) { Busy = "받는 중…"; Notify(); }
        try
        {
            var r = await c.ReceiveAsync(it, inline, true, ct);
            if (ct.IsCancellationRequested) return;
            if (r.Entries is null) { SetMessage("서버에서 삭제된 항목입니다"); return; }
            if (!await Write(r.Entries, it.Id, cacheImage: true, ct)) return;
            Log.Info($"applied seq={it.Seq} id={it.Id} kinds={string.Join(',', r.Kinds)}");
            settings.LastAppliedSeq = Math.Max(settings.LastAppliedSeq ?? 0, it.Seq); settings.Save();
            if (r.Entries.Any(e => e.Type is 3 or 4)) _ = DeleteAfterReceipt(c, it.Id);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            SetMessage("적용 실패: " + e.Message); Log.Error($"apply id={it.Id}: {e.Message}");
        }
        catch (Exception) { /* 취소됨 */ }
        finally
        {
            if (large) { Busy = null; Notify(); }
        }
    }

    /// 엔트리를 클립보드에 쓴다. 파일은 캐시에 다 쓴 뒤 CF_HDROP, 이미지는 PNG+DIBV5(+cacheImage면 번들 캐시), 텍스트는 D-49.
    /// 성공하면 true. 쓴 내용은 우리 것이므로 링과 시퀀스 번호를 기록한다 (D-24, D-58).
    async Task<bool> Write(List<BundleEntry> entries, string id, bool cacheImage, CancellationToken ct)
    {
        var files = entries.Where(e => e.Type == 4).ToList();
        if (files.Count > 0)
        {
            // 캐시에 모두 쓴 뒤 클립보드에 기록한다 (붙여넣기 시점에 파일이 완성돼 있어야 함, D-56). 이름은 D-57로 정리.
            var written = await Task.Run(() => WriteIncoming(id, files), ct);
            if (ct.IsCancellationRequested) return false;
            ring.Add(written.Hash);
            lastWrittenSeq = ClipboardIO.WriteFiles(listener.Handle, written.Paths, id);
        }
        else if (entries.FirstOrDefault(e => e.Type == 3) is { } png)
        {
            // 디코드·DIBV5 생성·픽셀 해시(D-54)·번들 캐시(D-62)는 UI 스레드 밖에서
            var (dib, hash) = await Task.Run(() =>
            {
                if (cacheImage)
                {
                    Directory.CreateDirectory(CacheDir(id));
                    File.WriteAllBytes(Path.Combine(CacheDir(id), "bundle.csb1"), Protocol.EncodeBundle(entries));
                }
                var bgra = ImageCodec.DecodePng(png.Data);
                return (Imaging.BgraToDibV5(bgra), Imaging.PixelHash(bgra));
            }, ct);
            if (ct.IsCancellationRequested) return false;
            ring.Add(hash);
            lastWrittenSeq = ClipboardIO.Write(listener.Handle, entries, id, dib);
        }
        else
        {
            var plain = entries.FirstOrDefault(e => e.Type == 1) is { } p ? Encoding.UTF8.GetString(p.Data) : null;
            var html = entries.FirstOrDefault(e => e.Type == 2) is { } h ? Encoding.UTF8.GetString(h.Data) : null;
            var hash = SyncLogic.ContentHash(plain, html);
            if (hash is not null) ring.Add(hash);   // 되돌아오는 변경을 업로드하지 않도록 (D-24)
            if (hash is not null && hash == ClipboardIO.CurrentTextHash(listener.Handle))
            {
                Log.Info($"apply skipped (same content) id={id}");   // D-49 (텍스트만)
                lastWrittenSeq = ClipboardIO.SequenceNumber;
            }
            else lastWrittenSeq = ClipboardIO.Write(listener.Handle, entries, id);
        }
        MarkOwn(lastWrittenSeq);
        return true;
    }

    static (List<string> Paths, string Hash) WriteIncoming(string itemId, List<BundleEntry> files)
    {
        var names = FileNames.Unique(files.Select(f => f.Name));
        var dir = Path.GetFullPath(CacheDir(itemId));
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var paths = new List<string>();
        foreach (var (name, f) in names.Zip(files))
        {
            var full = Path.GetFullPath(Path.Combine(dir, name));
            if (!string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("path escape: " + name);   // 경로 탈출 방지(이중 확인)
            File.WriteAllBytes(full, f.Data);
            paths.Add(full);
        }
        return (paths, FileNames.FilesHash(names.Zip(files, (n, f) => (n, f.Data))));
    }

    /// 수신 후 삭제 (D-61): 404/410은 완료, 그 밖의 실패는 로그만 (서버 24h 만료가 안전망).
    static async Task DeleteAfterReceipt(ServerClient c, string id)
    {
        try { await c.DeleteBodyAsync(id); Log.Info($"deleted body id={id}"); }
        catch (ServerException e) when (e.Status is 404 or 410) { Log.Info($"delete body id={id}: already gone ({e.Status})"); }
        catch (Exception e) { Log.Error($"delete body id={id}: {e.Message}"); }
    }

    /// 최근 항목 메뉴에서 선택: 서버에 본문이 있으면 받아서 적용(미디어면 이후 삭제), 없으면 캐시에서 복원 (6.3-6, D-62).
    public async void Restore(RecentItem item)
    {
        if (client is not { } c || cts is not { } token) return;
        try
        {
            if (!item.Purged && (await c.ListAsync(Math.Max(0, item.Seq - 1), token.Token)).FirstOrDefault(i => i.Id == item.Id) is { Purged: false } it)
            {
                await Apply(c, it, null, token.Token);
                return;
            }
            if (HasCache(item.Id)) { await RestoreFromCache(item.Id, token.Token); return; }
            SetMessage("서버에서 삭제됨");
        }
        catch (Exception e) { Log.Error("restore: " + e.Message); }
    }

    async Task RestoreFromCache(string id, CancellationToken ct)
    {
        var dir = CacheDir(id);
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);   // LRU
        var bundle = Path.Combine(dir, "bundle.csb1");
        if (File.Exists(bundle))
            await Write(Protocol.DecodeBundle(await File.ReadAllBytesAsync(bundle, ct)), id, cacheImage: false, ct);
        else
        {
            var paths = Directory.GetFiles(dir).Order(StringComparer.Ordinal).ToList();
            if (paths.Count == 0) { SetMessage("캐시가 비어 있습니다"); return; }
            lastWrittenSeq = ClipboardIO.WriteFiles(listener.Handle, paths, id);
            MarkOwn(lastWrittenSeq);
        }
        Log.Info($"restored from cache id={id}");
    }

    // ---- 캐시 정리 (D-62) ----

    async Task CleanupCache()
    {
        // 현재 클립보드가 우리 항목이면 그 폴더는 지우지 않는다 (붙여넣기할 파일이 사라지지 않도록)
        var marker = ClipboardIO.ReadFormat(listener.Handle, ClipboardIO.FmtMarker);
        var protectedId = marker is { Length: 16 } ? Convert.ToHexStringLower(marker) : null;
        try
        {
            var deleted = await Task.Run(() =>
            {
                if (!Directory.Exists(CacheRoot)) return 0;
                var entries = new List<CacheEntry>();
                foreach (var dir in new DirectoryInfo(CacheRoot).EnumerateDirectories())
                {
                    // 직계, 32자 hex, 심볼릭 링크/정션 아님
                    if (!CacheCleanup.IsCacheId(dir.Name) || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    var bytes = dir.EnumerateFiles().Sum(f => f.Length);
                    entries.Add(new CacheEntry(dir.Name, dir.LastWriteTimeUtc, bytes));
                }
                var plan = CacheCleanup.Plan(entries, DateTimeOffset.UtcNow, protectedId);
                foreach (var id in plan) Directory.Delete(CacheDir(id), recursive: true);
                return plan.Count;
            });
            if (deleted > 0) Log.Info($"cache cleanup removed {deleted} item(s)");
        }
        catch (Exception e) { Log.Error("cache cleanup: " + e.Message); }
    }

    // ---- 감시 / 송신 ----

    void OnClipboardChanged()
    {
        var seq = ClipboardIO.SequenceNumber;
        if (seq == lastWrittenSeq) return;   // 우리가 쓴 변경 (D-44)
        if (!CanSend || client is not { } c) return;   // 꺼져 있으면 보내지 않고 pending도 만들지 않는다 (6.2-3)

        if (ClipboardIO.GetShape(listener.Handle) is not { } shape) { Log.Error("clipboard busy (OpenClipboard failed)"); return; }
        if (shape.Own) { MarkOwn(seq); return; }
        switch (Media.Classify(shape.HasFiles, shape.HasImage, shape.HasText, Config))
        {
            case SendDecision.Ignore:
                return;   // 타입 off 등은 조용히 무시 (4.5)
            case SendDecision.Files:
            {
                var paths = ClipboardIO.ReadFiles(listener.Handle, out var busy);
                if (busy) { Log.Error("clipboard busy (OpenClipboard failed)"); return; }
                if (paths is { Count: > 0 }) _ = SendFiles(paths, seq, c);
                return;
            }
            case SendDecision.Text:
            {
                var content = ClipboardIO.ReadText(listener.Handle, out var busy);
                if (busy) { Log.Error("clipboard busy (OpenClipboard failed)"); return; }
                if (content is not null) SendText(content, seq, c);
                return;
            }
            case SendDecision.Image or SendDecision.ImageWithText:
            {
                var raw = ClipboardIO.ReadImage(listener.Handle, out var busy);
                var text = shape.HasText && !busy ? ClipboardIO.ReadText(listener.Handle, out busy) : null;
                if (busy) { Log.Error("clipboard busy (OpenClipboard failed)"); return; }
                if (raw is not null) _ = SendImage(raw, text, seq, c);
                return;
            }
        }
    }

    static (List<BundleEntry> Entries, List<string> Kinds) TextEntries(ClipboardIO.Content? content)
    {
        var entries = new List<BundleEntry>();
        var kinds = new List<string>();
        if (content?.Plain is { } plain) { entries.Add(new BundleEntry(1, "", Encoding.UTF8.GetBytes(plain))); kinds.Add("text"); }
        if (content?.Html is { } html) { entries.Add(new BundleEntry(2, "", Encoding.UTF8.GetBytes(html))); kinds.Add("html"); }
        return (entries, kinds);
    }

    void SendText(ClipboardIO.Content content, uint observed, ServerClient c)
    {
        if (SyncLogic.ContentHash(content.Plain, content.Html) is not { } hash) return;
        if (ring.Contains(hash)) { MarkOwn(observed); return; }   // 방금 주고받은 내용 = 우리 것
        ring.Add(hash);
        var (entries, kinds) = TextEntries(content);
        if (entries.Sum(e => e.Data.Length) > VaultConfig.TextLimit) { SetMessage("1 MiB를 넘는 텍스트는 보내지 않습니다", alert: true); return; }
        Enqueue(new Outgoing(c.Prepare(entries, kinds, content.Plain), [.. kinds], Protocol.PreviewOf(content.Plain ?? ""), observed), c);
    }

    /// 이미지: DIB→PNG 변환·픽셀 해시·봉인은 UI 스레드 밖에서 한다.
    async Task SendImage(ClipboardIO.RawImage raw, ClipboardIO.Content? text, uint observed, ServerClient c)
    {
        var limit = Config.MaxMediaBytes;
        try
        {
            var norm = await Task.Run(() => ImageCodec.Normalize(raw));
            if (norm is not { } n || Media.PngSize(n.Png) is not { } size) { Log.Error("image conversion failed"); return; }
            var hash = await Task.Run(() => Imaging.PixelHash(n.Pixels));
            if (ring.Contains(hash)) { MarkOwn(observed); return; }
            ring.Add(hash);
            var (entries, kinds) = TextEntries(text);
            entries.Insert(0, new BundleEntry(3, "", n.Png));
            kinds.Insert(0, "image");
            var total = entries.Sum(e => (long)e.Data.Length);
            if (total > limit)
            {
                SetMessage($"이미지가 {limit >> 20} MB를 넘어 보내지 않았습니다 ({total / 1048576.0:0.0} MB)", alert: true);
                Log.Info($"image too large bytes={total} limit={limit}");
                return;
            }
            var preview = text?.Plain is { } p ? Protocol.PreviewOf(p) : $"이미지 {size.W}×{size.H}";
            var item = await Task.Run(() => c.Prepare(entries, kinds, text?.Plain, size));
            Enqueue(new Outgoing(item, [.. kinds], preview, observed), c);
        }
        catch (Exception e) { Log.Error("send image: " + e.Message); }
    }

    /// 파일: 폴더 검사와 크기 합계 검사를 **읽기 전에** 하고, 읽기·해시·봉인은 UI 스레드 밖에서 (D-56).
    async Task SendFiles(List<string> paths, uint observed, ServerClient c)
    {
        var limit = Config.MaxMediaBytes;
        try
        {
            if (paths.Any(Directory.Exists)) { SetMessage("폴더가 포함된 복사는 보내지 않습니다", alert: true); return; }   // D-31
            var missing = paths.FirstOrDefault(p => !File.Exists(p));
            if (missing is not null) { SetMessage("파일을 읽지 못했습니다: " + Path.GetFileName(missing), alert: true); return; }
            var total = paths.Sum(p => new FileInfo(p).Length);
            if (total > limit) { SetMessage($"파일이 {limit >> 20} MB를 넘어 보내지 않았습니다 ({total / 1048576.0:0.0} MB)", alert: true); return; }
            var files = await Task.Run(() => paths.Select(p =>
            {
                // Office 등이 연 파일도 읽을 수 있게 공유 모드로 연다
                using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                return (Name: Path.GetFileName(p).Normalize(NormalizationForm.FormC), Data: ms.ToArray());   // D-25: NFC
            }).ToList());
            var hash = FileNames.FilesHash(files.Select(f => (f.Name, f.Data)));
            if (ring.Contains(hash)) { MarkOwn(observed); return; }
            ring.Add(hash);
            var preview = string.Join(", ", files.Select(f => f.Name));
            var item = await Task.Run(() => c.Prepare(files.Select(f => new BundleEntry(4, f.Name, f.Data)), ["files"], preview,
                files: files.Select(f => (f.Name, (long)f.Data.Length)).ToList()));
            Enqueue(new Outgoing(item, ["files"], preview, observed), c);
        }
        catch (Exception e) { SetMessage("파일을 읽지 못했습니다: " + e.Message, alert: true); Log.Error("send files: " + e.Message); }
    }

    /// 순서대로 한 개씩 (직렬 체인).
    void Enqueue(Outgoing o, ServerClient c)
    {
        var prev = sendChain;
        sendChain = Chain();
        async Task Chain()
        {
            try { await prev; } catch { /* 앞 작업의 실패는 이 작업과 무관 */ }
            if (cts is { } t) await Upload(o, c, t.Token);
        }
    }

    /// 재연결 직후 pending을 송신 큐로 올리고 끝날 때까지 기다린다. 있었으면 true (그 catch-up은 적용하지 않음, D-59).
    async Task<bool> FlushPending()
    {
        if (pending is not { } p || client is not { } c) return false;
        pending = null;
        Log.Info($"uploading pending id={p.Item.Id}");
        Enqueue(p, c);
        try { await sendChain; } catch { }
        return true;
    }

    /// 지수 백오프로 최대 3회. 같은 PreparedItem을 재전송하므로 중복 항목이 생기지 않는다 (idempotent).
    /// 전송 계층 실패로 끝나면 pending으로 보관한다 (D-59).
    async Task Upload(Outgoing o, ServerClient c, CancellationToken ct)
    {
        var item = o.Item;
        var large = item.SealedBody.Length > 1 << 20;
        if (large) { Busy = "보내는 중…"; Notify(); }
        try
        {
            var delay = 1.0;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var seq = await c.UploadAsync(item, ct);
                    Log.Info($"sent id={item.Id} seq={seq} kinds={string.Join(',', o.Kinds)} bytes={item.SealedBody.Length}");
                    // 서버는 업로더의 소켓에 이벤트를 보내지 않으므로 내가 보낸 항목을 최근 항목에 직접 추가한다.
                    AddRecent(new RecentItem(seq, item.Id, settings.DeviceId, o.Kinds, o.Preview, item.CreatedAt, false));
                    if (pending is { } p && p.Observed <= o.Observed)
                    {
                        Log.Info($"pending id={p.Item.Id} superseded by id={item.Id}");   // 더 새 항목이 올라갔으니 옛 pending은 버린다
                        pending = null;
                    }
                    MarkOwn(o.Observed);
                    SetMessage(null);
                    return;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    Log.Error($"upload id={item.Id} attempt={attempt}: {e.Message}");
                    var transport = ServerClient.IsTransportFailure(e, ct);
                    if (attempt == 3 || !transport)
                    {
                        if (transport)
                        {
                            if ((pending?.Observed ?? 0) <= o.Observed) pending = o;
                            Log.Info($"kept as pending id={item.Id}");
                            SetMessage("오프라인: 마지막 복사를 보관했다가 다시 연결되면 보냅니다", alert: !outageAlerted);
                            outageAlerted = true;
                        }
                        else SetMessage("전송 실패: " + e.Message, alert: true);
                        return;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                    delay *= 2;
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }   // Stop() 중 취소·해제 예외는 무시
        finally
        {
            if (large) { Busy = null; Notify(); }
        }
    }
}
