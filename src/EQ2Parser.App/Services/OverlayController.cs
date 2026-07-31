using EQ2Parser.App.Views;

namespace EQ2Parser.App.Services;

/// <summary>
/// Owns the in-game overlay window: show/hide, the locked (click-through)
/// state, and persisting position + visibility across sessions.
/// </summary>
public sealed class OverlayController(SourceManager manager)
{
    private OverlayWindow? _window;

    /// <summary>Keep the Timers page toggles honest when the overlay's own
    /// buttons (lock, close) change state.</summary>
    public event Action<bool>? VisibleChanged;
    public event Action<bool>? LockChanged;

    public void Show()
    {
        if (_window is null)
        {
            _window = new OverlayWindow(manager, this);
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        Persist(visible: true);
        VisibleChanged?.Invoke(true);
    }

    public void Hide()
    {
        _window?.Close();
        _window = null;
        Persist(visible: false);
        VisibleChanged?.Invoke(false);
    }

    public void SetLocked(bool locked)
    {
        _window?.ApplyLock(locked);
        manager.Settings = manager.Settings with { OverlayLocked = locked };
        manager.Settings.Save();
        LockChanged?.Invoke(locked);
    }

    /// <summary>Called by the window after a drag so position survives.</summary>
    public void SavePosition(double left, double top)
    {
        manager.Settings = manager.Settings with { OverlayLeft = left, OverlayTop = top };
        manager.Settings.Save();
    }

    private void Persist(bool visible)
    {
        manager.Settings = manager.Settings with { OverlayVisible = visible };
        manager.Settings.Save();
    }

    /// <summary>Restore the overlay at startup if it was open last session.</summary>
    public void RestoreFromSettings()
    {
        if (manager.Settings.OverlayVisible)
            Show();
    }
}
