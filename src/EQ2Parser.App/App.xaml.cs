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
        _manager.RestoreFromSettings();
        var window = new MainWindow(new MainViewModel(_manager));
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _manager?.PersistSources();
        _manager?.Dispose();
        base.OnExit(e);
    }
}
