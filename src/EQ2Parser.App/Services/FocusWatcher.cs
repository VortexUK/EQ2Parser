using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EQ2Parser.App.Services;

/// <summary>
/// Watches which process owns the system foreground window — the overlay
/// auto-hide's signal. Event-driven via SetWinEventHook
/// (EVENT_SYSTEM_FOREGROUND), so there is no polling; Windows calls back
/// on the installing thread's message pump, which is why this must be
/// created on the WPF UI thread (and why consumers may touch windows
/// directly in the event).
/// </summary>
public sealed class FocusWatcher : IDisposable
{
    /// <summary>Raised on every foreground switch: true when the new
    /// foreground process is "friendly" (this app — overlays included — or
    /// the EQ2 client), false for anything else (Discord, browser, …).</summary>
    public event Action<bool>? ForegroundFriendlyChanged;

    private static readonly string[] FriendlyProcessNames = ["EverQuest2", "EQ2"];

    // The marshalled delegate MUST live in a field: if the GC collects it,
    // the native hook calls into freed memory and the app dies at random.
    private readonly WinEventDelegate _callback;
    private readonly IntPtr _hook;
    private readonly int _ownPid = Environment.ProcessId;

    // Verdict per pid so a busy alt-tabber doesn't re-open Process handles
    // constantly. Pids recycle, so the cache is bounded and periodically
    // dropped rather than trusted forever.
    private readonly Dictionary<uint, bool> _verdicts = [];

    public FocusWatcher()
    {
        _callback = OnWinEvent;
        _hook = SetWinEventHook(
            EventSystemForeground, EventSystemForeground,
            IntPtr.Zero, _callback, idProcess: 0, idThread: 0, WinEventOutOfContext);
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int objectId, int childId, uint thread, uint time)
    {
        try
        {
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
                return;
            if (!_verdicts.TryGetValue(pid, out var friendly))
            {
                friendly = IsFriendly(pid);
                if (_verdicts.Count > 128)
                    _verdicts.Clear();
                _verdicts[pid] = friendly;
            }
            ForegroundFriendlyChanged?.Invoke(friendly);
        }
        catch (Exception)
        {
            // A hook callback that throws would take the app down from
            // native code — never let anything escape.
        }
    }

    private bool IsFriendly(uint pid)
    {
        if (pid == (uint)_ownPid)
            return true;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return FriendlyProcessNames.Any(n =>
                string.Equals(process.ProcessName, n, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false; // process already exited — whatever replaces it will re-fire
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
            UnhookWinEvent(_hook);
    }

    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;

    private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
