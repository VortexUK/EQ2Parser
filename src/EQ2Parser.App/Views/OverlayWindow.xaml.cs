using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using EQ2Parser.App.Services;
using EQ2Parser.App.ViewModels;

namespace EQ2Parser.App.Views;

/// <summary>
/// The in-game timer overlay: frameless, topmost, and click-through when
/// locked (WS_EX_TRANSPARENT — the game receives every click as if the
/// overlay weren't there). Unlocked it shows a drag header for placement.
/// Bars refresh at ~130 ms off a snapshot taken under the manager's lock.
/// </summary>
public partial class OverlayWindow
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x80;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hwnd, int index, int value);

    private readonly SourceManager _manager;
    private readonly OverlayController _controller;
    private readonly DispatcherTimer _tick;
    private readonly ObservableCollection<TimerBarRow> _bars = [];
    private bool _locked;

    public OverlayWindow(SourceManager manager, OverlayController controller)
    {
        _manager = manager;
        _controller = controller;
        InitializeComponent();
        Bars.ItemsSource = _bars;

        if (manager.Settings.OverlayLeft is { } left && manager.Settings.OverlayTop is { } top)
        {
            Left = left;
            Top = top;
        }
        else
        {
            Left = SystemParameters.WorkArea.Right - Width - 40;
            Top = SystemParameters.WorkArea.Top + 120;
        }

        SourceInitialized += (_, _) =>
        {
            // Never steal focus from the game, never show in alt-tab.
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GwlExstyle, GetWindowLong(hwnd, GwlExstyle) | WsExNoActivate | WsExToolWindow);
            ApplyLock(manager.Settings.OverlayLocked);
        };

        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(130),
        };
        _tick.Tick += (_, _) => Refresh();
        _tick.Start();
        Closed += (_, _) => _tick.Stop();
        Refresh();
    }

    private void Refresh()
    {
        var bars = _manager.SpellTimers.Snapshot(DateTimeOffset.Now);
        if (bars.Count > 12)
            bars.RemoveRange(12, bars.Count - 12);
        while (_bars.Count > bars.Count)
            _bars.RemoveAt(_bars.Count - 1);
        while (_bars.Count < bars.Count)
            _bars.Add(new TimerBarRow());
        for (var i = 0; i < bars.Count; i++)
            TimersViewModel.ApplyBar(_bars[i], bars[i]);

        EmptyHint.Visibility = bars.Count == 0 && !_locked ? Visibility.Visible : Visibility.Collapsed;
        // Locked and idle: fade the chrome away entirely — pixels appear
        // only when a timer is running.
        Root.Opacity = _locked && bars.Count == 0 ? 0 : 1;
    }

    /// <summary>Locked = click-through + no chrome; unlocked = draggable
    /// with the header visible.</summary>
    public void ApplyLock(bool locked)
    {
        _locked = locked;
        Header.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        Root.BorderBrush = locked
            ? System.Windows.Media.Brushes.Transparent
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x66, 0xC8, 0xA9, 0x6E));
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
        {
            var style = GetWindowLong(hwnd, GwlExstyle);
            SetWindowLong(hwnd, GwlExstyle, locked ? style | WsExTransparent : style & ~WsExTransparent);
        }
        Refresh();
    }

    private void Root_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_locked)
            return;
        DragMove();
        _controller.SavePosition(Left, Top);
    }

    private void Lock_Click(object sender, RoutedEventArgs e) => _controller.SetLocked(true);

    private void Close_Click(object sender, RoutedEventArgs e) => _controller.Hide();
}
