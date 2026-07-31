using System.IO;
using System.Text.Json;

namespace EQ2Parser.App.Services;

public sealed record SourceSetting(string Path, bool ParseFromStart);

/// <summary>Persisted app settings — %LocalAppData%\EQ2Parser\settings.json.</summary>
public sealed record AppSettings
{
    public List<SourceSetting> Sources { get; init; } = [];
    public double IdleEndSeconds { get; init; } = 6;
    public int PollMilliseconds { get; init; } = 10;

    /// <summary>Days of parse history kept on disk (pruned at startup).</summary>
    public int HistoryRetentionDays { get; init; } = 14;

    // Alert audio: null voice = best available (natural voice if exposed).
    public string? TtsVoiceId { get; init; }
    public double TtsRate { get; init; } = 1.0;
    public double AlertVolume { get; init; } = 1.0;

    // In-game timer overlay: null position = top-right of the primary screen.
    public bool OverlayVisible { get; init; }
    public bool OverlayLocked { get; init; }
    public double? OverlayLeft { get; init; }
    public double? OverlayTop { get; init; }

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EQ2Parser");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Corrupt settings never block startup — fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
