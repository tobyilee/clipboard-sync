using System.Text;
using System.Threading.Channels;
using ClipSync.Core;

namespace ClipSync.App;

/// 감시 → 송신, WebSocket → 수신 적용 (spec 6.2~6.5), vault 설정(4.4, D-50; Windows는 읽기 전용). mac SyncEngine.swift 와 같은 규칙.
/// 모든 메서드는 UI(STA) 스레드에서 호출되고, await 후에도 UI 스레드로 돌아온다 (Core는 ConfigureAwait(false)).
/// 로컬 우선·pending·수신 후 삭제는 M6, 파일은 M5b.
sealed class SyncEngine : IDisposable
{
    public enum Connection { Off, Connecting, Connected, Offline }

    public sealed record RecentItem(long Seq, string Id, string DeviceId, IReadOnlyList<string> Kinds, string Preview, ulong CreatedAt, bool Purged);

    sealed record Outgoing(PreparedItem Item, string[] Kinds, string Preview);

    readonly Settings settings;
    readonly ClipboardListener listener = new();
    readonly System.Windows.Forms.Timer debounce = new() { Interval = 100 };   // spec 8: ~100ms debounce
    readonly RecentHashRing ring = new();

    ServerClient? client;
    CancellationTokenSource? cts;
    CancellationTokenSource? applyCts;
    Channel<Outgoing>? uploads;
    uint lastWrittenSeq;

    public Connection State { get; private set; } = Connection.Off;
    public List<RecentItem> Recent { get; } = [];
    public string? LastMessage { get; private set; }
    /// "보내는 중…"/"받는 중…" (D-53)
    public string? Busy { get; private set; }
    public VaultConfig Config => settings.VaultConfig;
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
        uploads = Channel.CreateUnbounded<Outgoing>();
        _ = RunLoop(client, cts.Token);
        _ = UploadLoop(client, uploads.Reader, cts.Token);
        Log.Info($"engine start device={settings.DeviceId} server={url.Host} config.v={settings.ConfigVersion}");
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
                            State = Connection.Connected; LastMessage = null; backoff = 1; Notify();
                            Log.Info($"connected hello.seq={h.Seq}");
                            if (h.VaultConfig is { } cfg) Accept(cfg, c);
                            await CatchUp(c, h.Seq, ct);
                            break;
                        case ServerEvent.Item it:
                            Log.Info($"event item seq={it.Value.Seq} id={it.Value.Id} from={it.Value.DeviceId[..8]} inline={it.InlineBody is not null} bytes={it.Value.BodySize}");
                            await Handle(c, [it.Value], new() { [it.Value.Id] = it.InlineBody }, true, ct);
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
            SetMessage("목록 조회 실패: " + e.Message);
            Log.Error("catch-up: " + e.Message);
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
        var plan = SyncLogic.PlanCatchUp(candidates, last, settings.DeviceId, apply && CanApply, Config.AllowedKinds);
        settings.LastSeq = plan.NewLastSeq; settings.Save();
        Notify();
        Log.Info($"handled {items.Count} item(s) lastSeq={plan.NewLastSeq} target={plan.Target?.Seq.ToString() ?? "-"}");
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

    /// 원격 항목을 클립보드에 적용한다. 수신 후 삭제(이미지/파일)는 M6.
    async Task Apply(ServerClient c, ServerItem it, byte[]? inline, CancellationToken ct)
    {
        var large = inline is null && it.BodySize > 1 << 20;
        if (large) { Busy = "받는 중…"; Notify(); }
        try
        {
            var r = await c.ReceiveAsync(it, inline, true, ct);
            if (ct.IsCancellationRequested) return;
            if (r.Entries is null) { SetMessage("서버에서 삭제된 항목입니다"); return; }
            var files = r.Entries.Where(e => e.Type == 4).ToList();
            if (files.Count > 0)
            {
                // 캐시에 모두 쓴 뒤 클립보드에 기록한다 (붙여넣기 시점에 파일이 완성돼 있어야 함, D-56). 이름은 D-57로 정리.
                var written = await Task.Run(() => WriteIncoming(it.Id, files), ct);
                if (ct.IsCancellationRequested) return;
                ring.Add(written.Hash);
                lastWrittenSeq = ClipboardIO.WriteFiles(listener.Handle, written.Paths, it.Id);
                Log.Info($"applied files seq={it.Seq} id={it.Id} count={written.Paths.Count}");
            }
            else if (r.Entries.FirstOrDefault(e => e.Type == 3) is { } png)
            {
                // 디코드·DIBV5 생성·픽셀 해시(D-54)는 UI 스레드 밖에서
                var (dib, hash) = await Task.Run(() =>
                {
                    var bgra = ImageCodec.DecodePng(png.Data);
                    return (Imaging.BgraToDibV5(bgra), Imaging.PixelHash(bgra));
                }, ct);
                if (ct.IsCancellationRequested) return;
                ring.Add(hash);
                lastWrittenSeq = ClipboardIO.Write(listener.Handle, r.Entries, it.Id, dib);
                Log.Info($"applied image seq={it.Seq} id={it.Id} bytes={png.Data.Length}");
            }
            else
            {
                var plain = r.Entries.FirstOrDefault(e => e.Type == 1) is { } p ? Encoding.UTF8.GetString(p.Data) : null;
                var html = r.Entries.FirstOrDefault(e => e.Type == 2) is { } h ? Encoding.UTF8.GetString(h.Data) : null;
                var hash = SyncLogic.ContentHash(plain, html);
                if (hash is not null) ring.Add(hash);   // 되돌아오는 변경을 업로드하지 않도록 (D-24)
                if (hash is not null && hash == ClipboardIO.CurrentTextHash(listener.Handle))
                    Log.Info($"apply skipped (same content) id={it.Id} seq={it.Seq}");   // D-49 (텍스트만)
                else
                {
                    lastWrittenSeq = ClipboardIO.Write(listener.Handle, r.Entries, it.Id);
                    Log.Info($"applied id={it.Id} seq={it.Seq} kinds={string.Join(',', r.Kinds)}");
                }
            }
            settings.LastAppliedSeq = Math.Max(settings.LastAppliedSeq ?? 0, it.Seq); settings.Save();
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

    static (List<string> Paths, string Hash) WriteIncoming(string itemId, List<BundleEntry> files)
    {
        var names = FileNames.Unique(files.Select(f => f.Name));
        var dir = Path.GetFullPath(Path.Combine(AppPaths.Root, "incoming", itemId));
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

    /// 최근 항목 메뉴에서 선택: 서버에 본문이 남아 있으면 받아서 복원한다.
    public async void Restore(RecentItem item)
    {
        if (client is not { } c || cts is not { } token) return;
        try
        {
            var it = (await c.ListAsync(Math.Max(0, item.Seq - 1), token.Token)).FirstOrDefault(i => i.Id == item.Id);
            if (it is null) { SetMessage("서버에서 삭제됨"); return; }
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

        if (ClipboardIO.GetShape(listener.Handle) is not { } shape) { Log.Error("clipboard busy (OpenClipboard failed)"); return; }
        if (shape.Own) return;
        switch (Media.Classify(shape.HasFiles, shape.HasImage, shape.HasText, Config))
        {
            case SendDecision.Ignore:
                return;   // 타입 off 등은 조용히 무시 (4.5)
            case SendDecision.Files:
            {
                var paths = ClipboardIO.ReadFiles(listener.Handle, out var busy);
                if (busy) { Log.Error("clipboard busy (OpenClipboard failed)"); return; }
                if (paths is { Count: > 0 }) _ = SendFiles(paths, c, q);
                return;
            }
            case SendDecision.Text:
            {
                var content = ClipboardIO.ReadText(listener.Handle, out var busy);
                if (busy) { Log.Error("clipboard busy (OpenClipboard failed)"); return; }
                if (content is not null) SendText(content, c, q);
                return;
            }
            case SendDecision.Image or SendDecision.ImageWithText:
            {
                var raw = ClipboardIO.ReadImage(listener.Handle, out var busy);
                var text = shape.HasText && !busy ? ClipboardIO.ReadText(listener.Handle, out busy) : null;
                if (busy) { Log.Error("clipboard busy (OpenClipboard failed)"); return; }
                if (raw is not null) _ = SendImage(raw, text, c, q);
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

    void SendText(ClipboardIO.Content content, ServerClient c, Channel<Outgoing> q)
    {
        if (SyncLogic.ContentHash(content.Plain, content.Html) is not { } hash || ring.Contains(hash)) return;
        ring.Add(hash);
        var (entries, kinds) = TextEntries(content);
        if (entries.Sum(e => e.Data.Length) > VaultConfig.TextLimit) { SetMessage("1 MiB를 넘는 텍스트는 보내지 않습니다", alert: true); return; }
        q.Writer.TryWrite(new Outgoing(c.Prepare(entries, kinds, content.Plain), [.. kinds], Protocol.PreviewOf(content.Plain ?? "")));
    }

    /// 이미지: DIB→PNG 변환·픽셀 해시·봉인은 UI 스레드 밖에서 한다.
    async Task SendImage(ClipboardIO.RawImage raw, ClipboardIO.Content? text, ServerClient c, Channel<Outgoing> q)
    {
        var limit = Config.MaxMediaBytes;
        try
        {
            var norm = await Task.Run(() => ImageCodec.Normalize(raw));
            if (norm is not { } n || Media.PngSize(n.Png) is not { } size) { Log.Error("image conversion failed"); return; }
            var hash = await Task.Run(() => Imaging.PixelHash(n.Pixels));
            if (ring.Contains(hash)) return;
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
            q.Writer.TryWrite(new Outgoing(item, [.. kinds], preview));
        }
        catch (Exception e) { Log.Error("send image: " + e.Message); }
    }

    /// 파일: 폴더 검사와 크기 합계 검사를 **읽기 전에** 하고, 읽기·해시·봉인은 UI 스레드 밖에서 (D-56).
    async Task SendFiles(List<string> paths, ServerClient c, Channel<Outgoing> q)
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
                return (Name: Path.GetFileName(p).Normalize(System.Text.NormalizationForm.FormC), Data: ms.ToArray());   // D-25: NFC
            }).ToList());
            var hash = FileNames.FilesHash(files.Select(f => (f.Name, f.Data)));
            if (ring.Contains(hash)) return;
            ring.Add(hash);
            var preview = string.Join(", ", files.Select(f => f.Name));
            var item = await Task.Run(() => c.Prepare(files.Select(f => new BundleEntry(4, f.Name, f.Data)), ["files"], preview,
                files: files.Select(f => (f.Name, (long)f.Data.Length)).ToList()));
            q.Writer.TryWrite(new Outgoing(item, ["files"], preview));
        }
        catch (Exception e) { SetMessage("파일을 읽지 못했습니다: " + e.Message, alert: true); Log.Error("send files: " + e.Message); }
    }

    /// 순서대로 한 개씩. 지수 백오프로 최대 3회, 같은 PreparedItem을 재전송하므로 중복 항목이 생기지 않는다 (idempotent).
    async Task UploadLoop(ServerClient c, ChannelReader<Outgoing> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var (item, kinds, preview) in reader.ReadAllAsync(ct))
            {
                var large = item.SealedBody.Length > 1 << 20;
                if (large) { Busy = "보내는 중…"; Notify(); }
                var delay = 1.0;
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        var seq = await c.UploadAsync(item, ct);
                        Log.Info($"sent id={item.Id} seq={seq} kinds={string.Join(',', kinds)} bytes={item.SealedBody.Length}");
                        // 서버는 업로더의 소켓에 이벤트를 보내지 않으므로 내가 보낸 항목을 최근 항목에 직접 추가한다.
                        AddRecent(new RecentItem(seq, item.Id, settings.DeviceId, kinds, preview, item.CreatedAt, false));
                        SetMessage(null);
                        break;
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        Log.Error($"upload id={item.Id} attempt={attempt}: {e.Message}");
                        if (attempt == 3) { SetMessage("전송 실패: " + e.Message, alert: true); break; }
                        await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                        delay *= 2;
                    }
                }
                if (large) { Busy = null; Notify(); }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }   // Stop() 중 취소·해제 예외는 무시
    }
}
