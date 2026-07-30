using System.Windows;
using System.Windows.Threading;
using EQ2Parser.App.ViewModels;

namespace EQ2Parser.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _tick;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // The coalescing refresh: engine state mutates at up-to-10ms cadence
        // on background threads; the visible page repaints at ~100ms.
        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _tick.Tick += (_, _) => viewModel.Tick();
        _tick.Start();
        Closed += (_, _) => _tick.Stop();
    }
}
