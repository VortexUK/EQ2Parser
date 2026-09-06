using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace EQ2Parser.App.Services;

/// <summary>
/// Evidence for deaths the CLR never sees. <see cref="CrashLog"/> catches
/// every MANAGED escape route, but a native access violation, a stack
/// overflow, an OOM kill, or an antivirus terminating the process leaves
/// the crashes folder empty — reported in the field as "the app just
/// closes, no error". Three nets, all best-effort and silent on failure:
///
///  1. Session journal (%LocalAppData%\EQ2Parser\session.log): coarse
///     breadcrumbs (startup stages, sources, raid-file writes, TTS calls)
///     plus a once-a-minute heartbeat with memory numbers. The last lines
///     before a dirty death say what the app was doing (and the heartbeat
///     trend catches OOM creep).
///  2. WER LocalDumps self-registration (HKCU — no admin): Windows itself
///     writes a minidump to crashes\dumps on any native crash or stack
///     overflow, exactly the deaths we can't catch in-process.
///  3. Startup post-mortem: a previous session.log without the clean-exit
///     mark means the last run died. Its journal is copied into the
///     crashes folder and the Windows Application event log is searched
///     for matching Application Error / .NET Runtime / WER entries
///     (faulting module + exception code) — the user just sends the
///     crashes folder instead of spelunking Event Viewer.
///
/// An empty postmortem (no event-log entry, no dump, journal simply stops)
/// is itself diagnostic: outright process termination, i.e. antivirus.
/// </summary>
public static class SessionJournal
{
    private static readonly object Gate = new();
    private const string CleanExitMark = "clean-exit";

    public static string FilePath => Path.Combine(AppSettings.Directory, "session.log");

    private static DateTime _lastHeartbeat;
    private static DateTime _lastTts;

    /// <summary>Call once, immediately after CrashLog.Install — everything
    /// here must survive a startup crash one line later.</summary>
    public static void Begin()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            var previous = ReadPreviousSession();
            lock (Gate)
            {
                File.WriteAllText(FilePath,
                    Line($"session start — EQ2Parser {CrashLog.Version} · {Environment.OSVersion} · .NET {Environment.Version}"));
            }
            RegisterCrashDumps();
            if (previous is not null)
                _ = Task.Run(() => WritePostmortem(previous));
        }
        catch
        {
            // The journal must never be the thing that breaks startup.
        }
    }

    /// <summary>Timestamped breadcrumb; cheap enough for coarse events
    /// (startup stages, source add/remove, command-file writes).</summary>
    public static void Mark(string what)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, Line(what));
        }
        catch
        {
        }
    }

    /// <summary>Piggybacks the shell tick: at most one line a minute, with
    /// the numbers that expose a leak marching toward an OOM kill.</summary>
    public static void Heartbeat(int sourceCount)
    {
        var now = DateTime.UtcNow;
        if (now - _lastHeartbeat < TimeSpan.FromMinutes(1))
            return;
        _lastHeartbeat = now;
        long workingSet;
        try
        {
            using var proc = Process.GetCurrentProcess();
            workingSet = proc.WorkingSet64;
        }
        catch
        {
            workingSet = 0;
        }
        Mark($"heartbeat ws={workingSet >> 20}MB gc={GC.GetTotalMemory(false) >> 20}MB sources={sourceCount}");
    }

    /// <summary>The native TTS engine is a prime silent-crash suspect —
    /// mark entry into it (rate-limited; the mark BEFORE the native call
    /// is the breadcrumb that survives).</summary>
    public static void MarkTts(string what)
    {
        var now = DateTime.UtcNow;
        if (now - _lastTts < TimeSpan.FromSeconds(10))
            return;
        _lastTts = now;
        Mark(what);
    }

    /// <summary>Call last in OnExit.</summary>
    public static void End() => Mark(CleanExitMark);

    private static string Line(string what) =>
        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {what}{Environment.NewLine}";

    /// <summary>The previous run's journal when it died dirty; null when
    /// there is nothing to report.</summary>
    private static string? ReadPreviousSession()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            var text = File.ReadAllText(FilePath);
            return text.Contains(CleanExitMark, StringComparison.Ordinal) || text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static void WritePostmortem(string previousJournal)
    {
        try
        {
            var report = new StringBuilder()
                .AppendLine("The previous EQ2Parser session ended WITHOUT a clean exit and left no")
                .AppendLine("managed crash report — a native crash, stack overflow, OOM kill, or an")
                .AppendLine("external process termination (antivirus). Below: the session journal's")
                .AppendLine("final breadcrumbs, then any matching Windows event-log entries. If the")
                .AppendLine("event-log section is empty and crashes\\dumps has no new .dmp, the")
                .AppendLine("process was terminated from outside (check the AV's protection history).")
                .AppendLine()
                .AppendLine("── previous session journal ──────────────────────────────────────────")
                .AppendLine(previousJournal.TrimEnd())
                .AppendLine()
                .AppendLine("── Windows Application event log (last 48h, EQ2Parser-related) ───────");
            HarvestEventLog(report);
            Directory.CreateDirectory(CrashLog.Directory);
            File.WriteAllText(
                Path.Combine(CrashLog.Directory, $"postmortem-{DateTime.Now:yyyy-MM-dd_HHmmss}.txt"),
                report.ToString());
        }
        catch
        {
        }
    }

    private static void HarvestEventLog(StringBuilder report)
    {
        try
        {
            using var log = new EventLog("Application");
            var cutoff = DateTime.Now.AddHours(-48);
            var entries = log.Entries;
            var found = 0;
            // Newest-first, bounded scan — the Application log can be huge.
            for (int i = entries.Count - 1, scanned = 0; i >= 0 && scanned < 3000 && found < 10; i--, scanned++)
            {
                EventLogEntry entry;
                try
                {
                    entry = entries[i];
                }
                catch (ArgumentException)
                {
                    break; // log rotated under us
                }
                if (entry.TimeGenerated < cutoff)
                    break;
                if (entry.Source is not ("Application Error" or ".NET Runtime" or "Windows Error Reporting"))
                    continue;
                if (!entry.Message.Contains("EQ2Parser", StringComparison.OrdinalIgnoreCase))
                    continue;
                found++;
                report.AppendLine()
                    .AppendLine($"[{entry.TimeGenerated:yyyy-MM-dd HH:mm:ss}] {entry.Source}")
                    .AppendLine(entry.Message.Trim());
            }
            if (found == 0)
                report.AppendLine("(none found — consistent with an external process kill)");
        }
        catch (Exception ex)
        {
            report.AppendLine($"(event log unreadable: {ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>Ask Windows Error Reporting for a local minidump on native
    /// deaths. HKCU works without admin and WER honours per-app LocalDumps
    /// keys there; DumpType 1 = minidump, keep the last five.</summary>
    private static void RegisterCrashDumps()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\EQ2Parser.exe");
            key.SetValue("DumpFolder", Path.Combine(CrashLog.Directory, "dumps"), RegistryValueKind.ExpandString);
            key.SetValue("DumpType", 1, RegistryValueKind.DWord);
            key.SetValue("DumpCount", 5, RegistryValueKind.DWord);
        }
        catch
        {
        }
    }
}
