using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;

namespace EQ2Parser.App.Services;

/// <summary>
/// Last-resort exception capture: every unhandled exception is written to
/// %LocalAppData%\EQ2Parser\crashes\ before anything else happens, so "the
/// app just died" always leaves evidence with a stack trace.
///
/// UI-thread exceptions are logged and swallowed — mid-raid, a broken
/// button beats a dead parser (ACT behaves the same way). Genuinely fatal
/// exceptions still take the process down, but log on the way out.
/// </summary>
public static class CrashLog
{
    public static string Directory => Path.Combine(AppSettings.Directory, "crashes");

    private const int KeepFiles = 30;
    private static DateTime _lastWrite;
    private static DateTime _lastDialog;

    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            var path = Write("ui-thread", e.Exception);
            // Handled BEFORE the dialog so a MessageBox failure can't re-crash.
            e.Handled = true;
            ShowDialog("EQ2Parser hit an unexpected error and kept running.", e.Exception, path);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            var path = Write("fatal", ex);
            if (ex is not null)
                ShowDialog("EQ2Parser hit a fatal error and has to close.", ex, path);
        };
        // Background tasks that die unobserved don't crash modern .NET —
        // they just vanish. Log them so silent failures are findable too.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("background-task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Never throws — the crash logger crashing is the one
    /// failure mode this class exists to prevent. Rate-limited so an
    /// exception storm (per-frame render errors) can't flood the disk.</summary>
    private static string? Write(string kind, Exception? ex)
    {
        try
        {
            var now = DateTime.Now;
            // The rate limit tames exception storms — but the FATAL report
            // is the process's last words and must always land, even when a
            // storm preceded the crash.
            if (kind != "fatal" && now - _lastWrite < TimeSpan.FromSeconds(5))
                return null;
            _lastWrite = now;
            System.IO.Directory.CreateDirectory(Directory);
            Prune();
            var path = Path.Combine(Directory, $"crash-{now:yyyy-MM-dd_HHmmss}-{kind}.txt");
            var report = new StringBuilder()
                .AppendLine($"EQ2Parser {Version} — {kind} exception at {now:yyyy-MM-dd HH:mm:ss}")
                .AppendLine($"{Environment.OSVersion} · .NET {Environment.Version}")
                .AppendLine()
                .Append(ex?.ToString() ?? "(no exception object)");
            File.WriteAllText(path, report.ToString());
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void ShowDialog(string headline, Exception ex, string? path)
    {
        var now = DateTime.Now;
        if (now - _lastDialog < TimeSpan.FromSeconds(30))
            return;
        _lastDialog = now;
        try
        {
            MessageBox.Show(
                $"{headline}\n\n{ex.Message}\n\nDetails saved to:\n{path ?? Directory}",
                "EQ2Parser — error report", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // A dead dialog is fine; the log file is what matters.
        }
    }

    private static void Prune()
    {
        try
        {
            foreach (var stale in new DirectoryInfo(Directory).GetFiles("crash-*.txt")
                         .OrderByDescending(f => f.CreationTimeUtc).Skip(KeepFiles - 1))
                stale.Delete();
        }
        catch
        {
            // Pruning is best-effort.
        }
    }

    private static string Version =>
        typeof(CrashLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CrashLog).Assembly.GetName().Version?.ToString()
        ?? "dev";
}
