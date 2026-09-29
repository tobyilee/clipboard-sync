using System.Diagnostics;
using ClipSync.Core;
using Microsoft.Win32;

namespace ClipSync.App;

/// NotifyIcon 트레이 앱 (spec 8, D-13). 메뉴는 열 때마다 새로 만든다.
sealed class TrayContext : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "ClipSync";

    readonly Settings settings = Settings.Load();
    readonly SyncEngine engine;
    readonly NotifyIcon tray = new() { Text = "ClipSync", Visible = true };
    readonly ContextMenuStrip menu = new();
    readonly Dictionary<(SyncEngine.Connection, bool), Icon> icons = new();
    OnboardingForm? onboarding;

    public TrayContext()
    {
        engine = new SyncEngine(settings);
        engine.Changed += UpdateIcon;
        engine.Alert += msg => tray.ShowBalloonTip(5000, "ClipSync", msg, ToolTipIcon.Warning);   // D-45
        menu.Opening += (_, e) => { BuildMenu(); e.Cancel = false; };
        tray.ContextMenuStrip = menu;
        tray.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            // 왼쪽 클릭에도 메뉴를 연다 (NotifyIcon의 비공개 ShowContextMenu 사용)
            typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(tray, null);
        };
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) engine.Start(); };   // 슬립/웨이크 즉시 재연결 (6.5)
        UpdateIcon();
        if (Configured() is null) ShowOnboarding(); else engine.Start();
        Log.Info("app started " + Application.ProductVersion);
    }

    (Uri Url, string Vault)? Configured()
    {
        if (settings.ServerUri is not { } url) return null;
        try { return CredentialStore.Load() is { } p ? (url, "") : null; } catch { return null; }
    }

    // ---- 아이콘 ----

    Icon IconFor(SyncEngine.Connection c)
    {
        var key = (c, c == SyncEngine.Connection.Connected && settings.IsPaused);
        if (icons.TryGetValue(key, out var i)) return i;
        var color = c switch
        {
            SyncEngine.Connection.Connected => settings.IsPaused ? Color.Goldenrod : Color.SeaGreen,
            SyncEngine.Connection.Connecting => Color.SteelBlue,
            SyncEngine.Connection.Offline => Color.OrangeRed,
            _ => Color.Gray,
        };
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var body = new SolidBrush(color);
            g.FillRectangle(body, 6, 4, 20, 26);
            using var clip = new SolidBrush(Color.White);
            g.FillRectangle(clip, 11, 2, 10, 5);
            using var line = new Pen(Color.White, 2);
            g.DrawLine(line, 10, 14, 22, 14);
            g.DrawLine(line, 10, 20, 22, 20);
        }
        // 상태별로 한 번만 만든다 (GetHicon 핸들은 캐시해 누수를 막는다). 연결됨은 일시정지 여부로 색이 다르다.
        return icons[key] = Icon.FromHandle(bmp.GetHicon());
    }

    void UpdateIcon()
    {
        tray.Icon = IconFor(engine.State);
        var state = engine.State switch
        {
            SyncEngine.Connection.Connected => settings.IsPaused ? "일시정지" : "연결됨",
            SyncEngine.Connection.Connecting => "연결 중",
            SyncEngine.Connection.Offline => "오프라인",
            _ => "꺼짐",
        };
        tray.Text = "ClipSync · " + state;
    }

    // ---- 메뉴 ----

    void BuildMenu()
    {
        menu.Items.Clear();
        if (Configured() is null)
        {
            menu.Items.Add(Label("설정 필요"));
            menu.Items.Add("설정…", null, (_, _) => ShowOnboarding());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료", null, (_, _) => Quit());
            return;
        }
        var dot = engine.State switch
        {
            SyncEngine.Connection.Connected => "● 연결됨",
            SyncEngine.Connection.Connecting => "○ 연결 중…",
            SyncEngine.Connection.Offline => "⚠ 오프라인 (재연결 중)",
            _ => "○ 꺼짐",
        };
        menu.Items.Add(Label($"{dot} · {settings.ServerUri?.Host}"));
        if (engine.LastMessage is { } m) menu.Items.Add(Label("⚠ " + m));

        menu.Items.Add(Toggle("동기화", settings.SyncEnabled, () =>
        {
            settings.SyncEnabled = !settings.SyncEnabled; settings.Save();
            if (settings.SyncEnabled) engine.Start(); else engine.Stop();   // 전체 off: WebSocket 종료, 감시 중지 (6.1)
        }));
        var pause = new ToolStripMenuItem(settings.IsPaused ? $"일시정지 중 ({PauseRemaining()})" : "일시정지");
        pause.DropDownItems.Add("15분", null, (_, _) => SetPause(TimeSpan.FromMinutes(15)));
        pause.DropDownItems.Add("1시간", null, (_, _) => SetPause(TimeSpan.FromHours(1)));
        if (settings.IsPaused) pause.DropDownItems.Add("해제", null, (_, _) => SetPause(null));
        menu.Items.Add(pause);
        menu.Items.Add(Toggle("보내기", settings.SendEnabled, () => { settings.SendEnabled = !settings.SendEnabled; settings.Save(); }));
        menu.Items.Add(Toggle("받기", settings.ReceiveEnabled, () => { settings.ReceiveEnabled = !settings.ReceiveEnabled; settings.Save(); }));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Label("동기화 대상: 텍스트 (Mac에서 변경)"));   // vault 설정은 읽기 전용 (spec 8), M5에서 config 반영
        var recent = new ToolStripMenuItem("최근 항목");
        if (engine.Recent.Count == 0) recent.DropDownItems.Add(Label("없음"));
        foreach (var r in engine.Recent)
        {
            var item = new ToolStripMenuItem(RecentTitle(r)) { Enabled = !r.Purged };
            var captured = r;
            item.Click += (_, _) => engine.Restore(captured);
            recent.DropDownItems.Add(item);
        }
        menu.Items.Add(recent);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Toggle("로그인 시 자동 시작", IsAutoStart(), ToggleAutoStart));
        menu.Items.Add("설정 다시 열기…", null, (_, _) => ShowOnboarding());
        menu.Items.Add("로그 폴더 열기", null, (_, _) => OpenLogFolder());
        menu.Items.Add("종료", null, (_, _) => Quit());
    }

    string RecentTitle(SyncEngine.RecentItem r)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds((long)r.CreatedAt).ToLocalTime().ToString("HH:mm");
        var src = r.DeviceId == settings.DeviceId ? "이 PC" : "기기 " + r.DeviceId[..4];
        var kind = r.Kinds.Contains("html") ? "HTML" : "텍스트";
        var text = r.Preview.Replace("\r", " ").Replace("\n", " ");
        if (text.Length > 40) text = text[..40] + "…";
        // & 는 메뉴에서 단축키 표시로 해석되므로 이스케이프
        return $"{time} · {kind} · {src}{(r.Purged ? " · 서버에서 삭제됨" : "")} {text}".Replace("&", "&&");
    }

    string PauseRemaining() =>
        settings.PausedUntil is { } t ? $"{Math.Max(1, (int)((t - DateTimeOffset.Now).TotalMinutes) + 1)}분 남음" : "";

    void SetPause(TimeSpan? d)
    {
        settings.PausedUntil = d is { } v ? DateTimeOffset.Now + v : null;
        settings.Save();
        UpdateIcon();
    }

    static ToolStripMenuItem Label(string t) => new(t) { Enabled = false };

    static ToolStripMenuItem Toggle(string t, bool on, Action a)
    {
        var i = new ToolStripMenuItem(t) { Checked = on };
        i.Click += (_, _) => a();
        return i;
    }

    // ---- 동작 ----

    void ShowOnboarding()
    {
        if (onboarding is { IsDisposed: false }) { onboarding.Activate(); return; }
        onboarding = new OnboardingForm(settings);
        onboarding.FormClosed += (_, _) =>
        {
            if (onboarding?.Saved == true) engine.Start();
            onboarding = null;
        };
        onboarding.Show();
        onboarding.Activate();
    }

    static bool IsAutoStart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) is string;
    }

    static void ToggleAutoStart()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (k.GetValue(RunValue) is string) k.DeleteValue(RunValue);
        else k.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
    }

    static void OpenLogFolder()
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDir}\"") { UseShellExecute = true });
    }

    void Quit()
    {
        Log.Info("app quit");
        engine.Dispose();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }
}
