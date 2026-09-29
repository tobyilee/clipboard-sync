namespace ClipSync.App;

static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardSync");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogDir => Path.Combine(Root, "logs");
}

/// 파일 로그 (D-46). **클립보드 내용·passphrase는 기록하지 않는다** — id, seq, 크기, 상태, 오류만.
static class Log
{
    static readonly object Gate = new();
    const long MaxBytes = 1 << 20;

    public static string FilePath => Path.Combine(AppPaths.LogDir, "clipsync.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                var f = new FileInfo(FilePath);
                if (f.Exists && f.Length > MaxBytes) File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {msg}{Environment.NewLine}");
            }
        }
        catch { /* 로그 실패는 무시 */ }
    }
}
