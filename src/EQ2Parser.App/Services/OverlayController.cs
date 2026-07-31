using EQ2Parser.App.Views;

namespace EQ2Parser.App.Services;

public enum OverlayKind
{
    MiniParseDps = 0,
    MiniParseHps = 1,
    MiniParseTank = 2,
    TimerA = 3,
    TimerB = 4,
}

/// <summary>
/// Owns the in-game overlay windows (three mini parse meters + the two
/// timer panels): show/hide, lock (click-through), and live-applied
/// appearance settings — everything persisted per window.
/// </summary>
public sealed class OverlayController(SourceManager manager)
{
    private readonly Dictionary<OverlayKind, OverlayShellWindow> _windows = [];

    /// <summary>Raised when any kind's settings change — the Overlays page
    /// mirrors its controls off this.</summary>
    public event Action<OverlayKind>? SettingsChanged;

    /// <summary>Mini parses are user-resizable (rows auto-fit the height);
    /// timer panels size themselves to their bars.</summary>
    public static bool IsResizable(OverlayKind kind) => kind
        is OverlayKind.MiniParseDps or OverlayKind.MiniParseHps or OverlayKind.MiniParseTank;

    public OverlayWindowSettings GetSettings(OverlayKind kind) => kind switch
    {
        OverlayKind.MiniParseDps => manager.Settings.MiniParseOverlay ?? new OverlayWindowSettings(),
        OverlayKind.MiniParseHps => manager.Settings.MiniParseHpsOverlay ?? new OverlayWindowSettings(),
        OverlayKind.MiniParseTank => manager.Settings.MiniParseTankOverlay ?? new OverlayWindowSettings(),
        OverlayKind.TimerB => manager.Settings.TimerOverlayB ?? new OverlayWindowSettings(),
        // Panel A inherits the legacy single-overlay fields on first run.
        _ => manager.Settings.TimerOverlayA ?? new OverlayWindowSettings
        {
            Visible = manager.Settings.OverlayVisible,
            Locked = manager.Settings.OverlayLocked,
            Left = manager.Settings.OverlayLeft,
            Top = manager.Settings.OverlayTop,
        },
    };

    /// <summary>Persist a settings change and push it onto the live window.</summary>
    public void Update(OverlayKind kind, Func<OverlayWindowSettings, OverlayWindowSettings> change)
    {
        var updated = change(GetSettings(kind));
        manager.Settings = kind switch
        {
            OverlayKind.MiniParseDps => manager.Settings with { MiniParseOverlay = updated },
            OverlayKind.MiniParseHps => manager.Settings with { MiniParseHpsOverlay = updated },
            OverlayKind.MiniParseTank => manager.Settings with { MiniParseTankOverlay = updated },
            OverlayKind.TimerB => manager.Settings with { TimerOverlayB = updated },
            _ => manager.Settings with { TimerOverlayA = updated },
        };
        manager.Settings.Save();
        if (_windows.TryGetValue(kind, out var window))
            window.Apply(updated);
        SettingsChanged?.Invoke(kind);
    }

    public void Show(OverlayKind kind)
    {
        if (!_windows.ContainsKey(kind))
        {
            var settings = GetSettings(kind);
            System.Windows.Controls.UserControl content = kind switch
            {
                OverlayKind.MiniParseDps => new MiniParseContent(manager, "DPS"),
                OverlayKind.MiniParseHps => new MiniParseContent(manager, "HPS"),
                OverlayKind.MiniParseTank => new MiniParseContent(manager, "Tanking"),
                OverlayKind.TimerB => new TimerPanelContent(manager, panel: 2),
                _ => new TimerPanelContent(manager, panel: 1),
            };
            var title = kind switch
            {
                OverlayKind.MiniParseDps => "DPS — drag, then lock",
                OverlayKind.MiniParseHps => "HEALING — drag, then lock",
                OverlayKind.MiniParseTank => "TANKING — drag, then lock",
                OverlayKind.TimerB => "TIMERS B — drag, then lock",
                _ => "TIMERS — drag, then lock",
            };
            var window = new OverlayShellWindow(this, kind, title, content, settings);
            window.Closed += (_, _) => _windows.Remove(kind);
            _windows[kind] = window;
            window.Show();
        }
        Update(kind, s => s with { Visible = true });
    }

    public void Hide(OverlayKind kind)
    {
        if (_windows.TryGetValue(kind, out var window))
        {
            _windows.Remove(kind);
            window.Close();
        }
        Update(kind, s => s with { Visible = false });
    }

    public void SetLocked(OverlayKind kind, bool locked) =>
        Update(kind, s => s with { Locked = locked });

    public void SavePosition(OverlayKind kind, double left, double top) =>
        Update(kind, s => s with { Left = left, Top = top });

    /// <summary>Grip resize on a resizable overlay.</summary>
    public void SaveSize(OverlayKind kind, double width, double height) =>
        Update(kind, s => s with { Width = width, Height = height });

    /// <summary>Restore whichever overlays were open last session.</summary>
    public void RestoreFromSettings()
    {
        foreach (var kind in Enum.GetValues<OverlayKind>())
        {
            if (GetSettings(kind).Visible)
                Show(kind);
        }
    }
}
