using ClipSync.App;

static class Program
{
    static int errorShown;

    /// 첫 오류는 대화상자로도 보여 준다 (Cloud PC의 로그 파일을 바로 볼 수 없어서 스크린샷으로 확인하기 위함).
    static void ReportError(string where, object error)
    {
        Log.Error($"unhandled ({where}): {error}");
        if (Interlocked.Exchange(ref errorShown, 1) == 0)
        {
            var first = error.ToString()?.Split('\n')[0] ?? "(unknown)";
            MessageBox.Show($"{where}: {first}\n\n로그: {Log.FilePath}", "ClipSync 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // `ClipSync.exe --selftest`: Win32 계층 자체 테스트 (클립보드를 덮어쓴다). 실제 passphrase 항목은 건드리지 않는다.
        if (args.Contains("--selftest")) return SelfTest.Run();

        // 같은 device_id로 이중 업로드/클립보드 경쟁을 막기 위해 한 번만 실행한다 (D-46).
        using var mutex = new Mutex(true, @"Local\ClipSync.SingleInstance", out var first);
        if (!first) return 0;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportError("UI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportError("app", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("unobserved task: " + e.Exception.GetBaseException().Message); e.SetObserved(); };

        // TrayContext 생성자에서 시작하는 async 루프와 SystemEvents 핸들러가 UI 스레드로 돌아오도록 먼저 설치한다.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        try
        {
            Application.Run(new TrayContext());
        }
        catch (Exception e)
        {
            ReportError("startup", e);
            return 1;
        }
        return 0;
    }
}
