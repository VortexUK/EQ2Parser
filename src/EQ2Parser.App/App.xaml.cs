using System.Windows;
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
        overlay.RestoreFromSettings();
        _ = _manager.Updates.CheckAndDownloadAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _manager?.PersistSources();
        _manager?.Dispose();
        base.OnExit(e);
    }
}
