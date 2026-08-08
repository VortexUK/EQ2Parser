using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.Views;

/// <summary>Content hosted by the overlay shell.</summary>
public interface IOverlayContent
{
    /// <summary>Called on the shell's tick; return true when there is
    /// something to show (locked + nothing → the shell fades out fully).</summary>
    bool Refresh(OverlayWindowSettings settings);
}

/// <summary>
/// The shared in-game overlay chrome: frameless glass card, topmost,
/// never-activating, draggable while unlocked, WS_EX_TRANSPARENT
/// click-through while locked, with opacity/scale/width applied live from
/// settings. Timer panels auto-size to their bars; mini parses are
/// grip-resizable (minimum 220×140) and their rows fill the space.
/// </summary>
public partial class OverlayShellWindow
{
    private const double MinOverlayWidth = 220;
    private const double MinOverlayHeight = 140;
    private const double DefaultResizableHeight = 260;

    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x80;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hwnd, int index, int value);

    private readonly OverlayController _controller;
    private readonly OverlayKind _kind;
    private readonly IOverlayContent _content;
    private readonly bool _resizable;
    private readonly DispatcherTimer _tick;
    private bool _locked;

    public OverlayShellWindow(OverlayController controller, OverlayKind kind, string title,
        System.Windows.Controls.UserControl content, OverlayWindowSettings settings)
    {
        _controller = controller;
        _kind = kind;
        _content = (IOverlayContent)content;
        _resizable = OverlayController.IsResizable(kind);
        InitializeComponent();
        TitleText.Text = title;
        Host.Content = content;

        SizeToContent = _resizable ? SizeToContent.Manual : SizeToContent.Height;
        if (_resizable)
            Height = settings.Height ?? DefaultResizableHeight;

        if (settings is { Left: { } left, Top: { } top })
        {
            Left = left;
            Top = top;
        }
        else
        {
            Left = SystemParameters.WorkArea.Right - settings.Width - 40 - 320 * ((int)kind % 3);
            Top = SystemParameters.WorkArea.Top + 120 + 90 * ((int)kind / 3);
        }

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _ = SetWindowLong(hwnd, GwlExstyle, GetWindowLong(hwnd, GwlExstyle) | WsExNoActivate | WsExToolWindow);
            Apply(_controller.GetSettings(_kind));
        };

        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _tick.Tick += (_, _) => RefreshContent();
        _tick.Start();
        Closed += (_, _) => _tick.Stop();
    }

    private void RefreshContent()
    {
        var settings = _controller.GetSettings(_kind);
        var hasContent = _content.Refresh(settings);
        // Locked + idle: zero pixels until something happens. Appearing
        // SNAPS (a starting fight wants its meter instantly); disappearing
        // eases out over ~0.75s of ticks — the post-fight fade.
        var visible = !_locked || hasContent;
        Root.Opacity = visible ? 1 : Math.Max(0, Root.Opacity - 0.2);
    }

    /// <summary>Push the current settings onto the live window.</summary>
    public void Apply(OverlayWindowSettings settings)
    {
        Width = settings.Width;
        if (_resizable)
            Height = settings.Height ?? DefaultResizableHeight;
        Opacity = settings.Opacity;
        Root.LayoutTransform = settings.Scale is > 0.99 and < 1.01
            ? null
            : new ScaleTransform(settings.Scale, settings.Scale);

        _locked = settings.Locked;
        HeaderRow.Visibility = _locked ? Visibility.Collapsed : Visibility.Visible;
        ResizeGrip.Visibility = _resizable && !_locked ? Visibility.Visible : Visibility.Collapsed;
        Hairline.BorderBrush = _locked
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(0x5A, 0xC8, 0xA9, 0x6E));
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
        {
            var style = GetWindowLong(hwnd, GwlExstyle);
            _ = SetWindowLong(hwnd, GwlExstyle, _locked ? style | WsExTransparent : style & ~WsExTransparent);
        }
        RefreshContent();
    }

    private void Root_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_locked)
            return;
        DragMove();
        _controller.SavePosition(_kind, Left, Top);
    }

    private void ResizeGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        // Scale affects visual size; divide the mouse delta back out so the
        // corner tracks the cursor.
        var scale = Root.LayoutTransform is ScaleTransform t ? t.ScaleX : 1.0;
        Width = Math.Clamp(Width + e.HorizontalChange / scale, MinOverlayWidth, 900);
        Height = Math.Clamp(Height + e.VerticalChange / scale, MinOverlayHeight, 1200);
    }

    private void ResizeGrip_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _controller.SaveSize(_kind, Width, Height);

    private void Lock_Click(object sender, RoutedEventArgs e) => _controller.SetLocked(_kind, true);

    private void Close_Click(object sender, RoutedEventArgs e) => _controller.Hide(_kind);
}
