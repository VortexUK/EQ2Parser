using System.Windows;
using System.Windows.Controls;
using EQ2Parser.App.Services;
using EQ2Parser.App.ViewModels;
using LiveChartsCore.SkiaSharpView;

namespace EQ2Parser.App;

public partial class App : Application
{
    private SourceManager? _manager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // First, before anything can throw: even a startup crash must leave
        // a log behind.
        CrashLog.Install(this);
        // Process-wide default match-timeout for every Regex built without an
        // explicit one (belt-and-suspenders for the grammar + any future
        // pattern). Trigger patterns set their own explicit timeout; the
        // real grammar-ReDoS defense is the log-line length cap in
        // LogTailReader. Must be set before any Regex is constructed.
        AppContext.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromMilliseconds(200));
        // The editors explain every option in its tooltip; WPF's default
        // ~5s auto-dismiss cuts them off mid-read.
        ToolTipService.ShowDurationProperty.OverrideMetadata(
            typeof(DependencyObject), new FrameworkPropertyMetadata(60_000));
        LiveChartsCore.LiveCharts.Configure(config => config.AddDarkTheme());
        _manager = new SourceManager();
        _manager.RestoreHistory();
        _manager.RestoreFromSettings();
        _manager.StartFolderWatch();
        var overlay = new OverlayController(_manager);
        var window = new MainWindow(new MainViewModel(_manager, overlay));
        MainWindow = window;
        // Overlays are windows too — without this, closing the main window
        // leaves the app running headless behind any open overlay.
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        // First run: a fresh install has no folders or sources — offer the
        // log-folder wizard once (accepted or skipped, never nag again).
        if (!_manager.Settings.FirstRunShown
            && _manager.Settings.WatchedFolders.Count == 0
            && _manager.Settings.Sources.Count == 0)
        {
            new Views.FirstRunWindow(_manager) { Owner = window }.ShowDialog();
            _manager.Settings = _manager.Settings with { FirstRunShown = true };
            _manager.Settings.Save();
        }
        overlay.RestoreFromSettings();
        _ = _manager.Updates.CheckAndDownloadAsync();
        _ = _manager.Lexicon.StartupAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _manager?.PersistSources();
        _manager?.Dispose();
        base.OnExit(e);
    }
}
