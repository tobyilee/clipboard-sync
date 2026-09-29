using System.Text.Json;
using System.Text.Json.Serialization;
using ClipSync.Core;

namespace ClipSync.App;

/// 기기별 설정 (spec 6.1). passphrase는 Credential Manager, 나머지는 %LOCALAPPDATA%\ClipboardSync\settings.json.
sealed class Settings
{
    [JsonPropertyName("serverUrl")] public string? ServerUrl { get; set; }
    [JsonPropertyName("deviceId")] public string? DeviceIdValue { get; set; }
    [JsonPropertyName("syncEnabled")] public bool SyncEnabled { get; set; } = true;
    [JsonPropertyName("sendEnabled")] public bool SendEnabled { get; set; } = true;
    [JsonPropertyName("receiveEnabled")] public bool ReceiveEnabled { get; set; } = true;
    [JsonPropertyName("pausedUntil")] public DateTimeOffset? PausedUntil { get; set; }
    /// 목록 워터마크(spec 6.1 last_seq). null이면 첫 실행 → hello.seq로 초기화 (D-42).
    [JsonPropertyName("lastSeq")] public long? LastSeq { get; set; }
    /// 실제로 적용한 가장 높은 seq (spec 6.1 last_applied_seq).
    [JsonPropertyName("lastAppliedSeq")] public long? LastAppliedSeq { get; set; }

    [JsonIgnore] public Uri? ServerUri => ServerUrl is null ? null : ServerUrlInput.Parse(ServerUrl);

    [JsonIgnore]
    public string DeviceId
    {
        get
        {
            if (DeviceIdValue is { Length: 32 } v && v.All(Uri.IsHexDigit) && v == v.ToLowerInvariant()) return v;
            DeviceIdValue = ServerClient.NewDeviceId();
            Save();
            return DeviceIdValue;
        }
    }

    [JsonIgnore] public bool IsPaused => PausedUntil is { } t && t > DateTimeOffset.Now;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new Settings();
        }
        catch (Exception e) { Log.Error("settings load: " + e.Message); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception e) { Log.Error("settings save: " + e.Message); }
    }
}
