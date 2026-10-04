using System.Text.Json;

namespace EchoSub.Desktop;

internal sealed record DesktopPreferences
{
    public int Version { get; init; } = 1;
    public string Language { get; init; } = "en";
    public string? DeviceId { get; init; }
    public double OverlayWidth { get; init; } = 760;
    public double CardOpacity { get; init; } = 0.75;
    public bool ShowSource { get; init; }
    public bool Partial { get; init; }
    public bool PartialTranslation { get; init; }
    public bool CosmeticRevisions { get; init; }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoSub", "settings.json");

    internal static DesktopPreferences Load(string? isolatedPath = null)
    {
        var path = isolatedPath ?? SettingsPath;
        if (!File.Exists(path)) return new();
        using var stream = File.OpenRead(path);
        if (stream.Length > 16_384) throw new IOException("설정 파일의 크기가 너무 큽니다.");
        var preferences = JsonSerializer.Deserialize<DesktopPreferences>(stream)
            ?? throw new IOException("설정 파일이 비어 있습니다.");
        if (preferences.Version != 1) throw new IOException("지원하지 않는 설정 파일 버전입니다.");
        return preferences with
        {
            Language = preferences.Language is "en" or "ja" or "ko" ? preferences.Language : "en",
            DeviceId = preferences.DeviceId is { Length: <= 2048 } ? preferences.DeviceId : null,
            OverlayWidth = double.IsFinite(preferences.OverlayWidth) ? Math.Clamp(preferences.OverlayWidth, 420, 1100) : 760,
            CardOpacity = double.IsFinite(preferences.CardOpacity) ? Math.Clamp(preferences.CardOpacity, 0.25, 1) : 0.75,
            Partial = preferences.Partial || preferences.PartialTranslation
        };
    }

    internal void Save(string? isolatedPath = null)
    {
        var path = isolatedPath ?? SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
