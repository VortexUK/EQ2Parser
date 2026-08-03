using System.IO;
using System.Text.Json;

namespace EQ2Parser.App.Services;

/// <summary>LastPosition: byte offset of the last consumed line — the next
/// session resumes there and catches up what was written while the app was
/// closed. Null (older settings files) = tail from the end. AutoDiscovered
/// sources came from a watched folder: their positions persist here but the
/// folder watcher (not RestoreFromSettings) re-adds them when active.</summary>
public sealed record SourceSetting(string Path, bool ParseFromStart, long? LastPosition = null, bool AutoDiscovered = false);

/// <summary>One overlay window's persisted shape. Null position = the
/// default spot on the primary screen. MaxItems = rows (mini parse) or
/// bars (timer panels).</summary>
public sealed record OverlayWindowSettings
{
    public bool Visible { get; init; }
    public bool Locked { get; init; }
    public double? Left { get; init; }
    public double? Top { get; init; }
    public double Width { get; init; } = 280;
    /// <summary>Only for resizable overlays (mini parses) — null = default.</summary>
    public double? Height { get; init; }
    public double Opacity { get; init; } = 0.95;
    public double Scale { get; init; } = 1.0;
    public int MaxItems { get; init; } = 10;

    /// <summary>Notifications overlay only: seconds a toast stays before
    /// fading out.</summary>
    public double ToastSeconds { get; init; } = 6;
}

/// <summary>Persisted app settings — %LocalAppData%\EQ2Parser\settings.json.</summary>
public sealed record AppSettings
{
    public List<SourceSetting> Sources { get; init; } = [];

    /// <summary>Folders scanned for eq2log_*.txt (recursively — the EQ2
    /// logs root covers every server subfolder): any log that becomes
    /// active is tracked automatically.</summary>
    public List<string> WatchedFolders { get; init; } = [];

    public double IdleEndSeconds { get; init; } = 6;
    public int PollMilliseconds { get; init; } = 10;

    /// <summary>Days of BOSS fights loaded into the parser at startup —
    /// older ones stay in the archive (never auto-deleted) and can be
    /// pulled back from the Archive window.</summary>
    public int HistoryBossDays { get; init; } = 7;

    /// <summary>Days of trash fights kept at all — loaded at startup within
    /// the window, hard-deleted past it.</summary>
    public int HistoryTrashDays { get; init; } = 1;

    // Alert audio: null voice = best available (natural voice if exposed).
    public string? TtsVoiceId { get; init; }
    public double TtsRate { get; init; } = 1.0;
    public double AlertVolume { get; init; } = 1.0;

    // Legacy single-overlay fields (pre-Overlays-page) — migrated into
    // TimerOverlayA on load, never written again.
    public bool OverlayVisible { get; init; }
    public bool OverlayLocked { get; init; }
    public double? OverlayLeft { get; init; }
    public double? OverlayTop { get; init; }

    /// <summary>Main-page fight-tree column width (the splitter position).
    /// Null = never dragged, use the XAML default.</summary>
    public double? TreeColumnWidth { get; init; }

    /// <summary>Visible encounter-grid columns (ColumnToggle keys, ACT's
    /// "Encounter View Options"). Null = never customised, use defaults.</summary>
    public List<string>? EncounterColumns { get; init; }

    /// <summary>Visible drill-table columns (ACT's "Combatant View Options").
    /// Null = never customised, use defaults.</summary>
    public List<string>? CombatantColumns { get; init; }

    /// <summary>Visible swing-log columns (ACT's "AttackType View Options").
    /// Null = never customised, use defaults.</summary>
    public List<string>? AttackTypeColumns { get; init; }

    /// <summary>Where the Lexicon trigger/timer pack syncs from. Point at a
    /// local dev server to test curation before it ships.</summary>
    public string LexiconBaseUrl { get; init; } = "https://varsoon.eq2lexicon.com";

    /// <summary>Auto-upload finished fights to EQ2Lexicon. Off until the
    /// user opts in AND a token is saved.</summary>
    public bool UploadEnabled { get; init; }

    /// <summary>EQ2Lexicon API token, DPAPI-encrypted for the current
    /// Windows user + base64 (see TokenProtector) — never plaintext, so a
    /// copied settings.json (or a quarantine copy) can't leak it.</summary>
    public string? LexiconApiTokenProtected { get; init; }

    /// <summary>Mass-detriment callouts ("8 players stunned").</summary>
    public bool CalloutsEnabled { get; init; } = true;
    public int CalloutMinPlayers { get; init; } = 3;
    public int CalloutCooldownSeconds { get; init; } = 12;

    // The overlay windows. Null = never configured (defaults apply; timer
    // panel A seeds from the legacy fields, the DPS meter from the old
    // single mini parse).
    public OverlayWindowSettings? MiniParseOverlay { get; init; }
    public OverlayWindowSettings? MiniParseHpsOverlay { get; init; }
    public OverlayWindowSettings? MiniParseTankOverlay { get; init; }
    public OverlayWindowSettings? TimerOverlayA { get; init; }
    public OverlayWindowSettings? TimerOverlayB { get; init; }
    public OverlayWindowSettings? NotificationsOverlay { get; init; }

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EQ2Parser");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load() =>
        PersistedJsonFile.Load(FilePath, static () => new AppSettings());

    public void Save() => PersistedJsonFile.Save(FilePath, this, JsonOptions);

    /// <summary>Debounced save for slider-drag callers — the factory runs
    /// at write time so the FINAL drag value is what lands on disk.</summary>
    public static void SaveSoon(Func<AppSettings> current) =>
        PersistedJsonFile.SaveSoon(FilePath, current, JsonOptions);
}
