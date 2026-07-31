using CommunityToolkit.Mvvm.ComponentModel;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.ViewModels;

/// <summary>One overlay window's configuration card. Every change applies
/// to the live window instantly and persists.</summary>
public sealed partial class OverlayConfigVm : ObservableObject
{
    private readonly OverlayController _controller;
    private readonly OverlayKind _kind;
    private bool _syncing;

    public string Title { get; }
    public string ItemsLabel { get; }

    public OverlayConfigVm(OverlayController controller, OverlayKind kind, string title, string itemsLabel)
    {
        _controller = controller;
        _kind = kind;
        Title = title;
        ItemsLabel = itemsLabel;
        SyncFrom(controller.GetSettings(kind));
        controller.SettingsChanged += changed =>
        {
            if (changed == kind)
                SyncFrom(controller.GetSettings(kind));
        };
    }

    private void SyncFrom(OverlayWindowSettings s)
    {
        _syncing = true;
        Visible = s.Visible;
        Locked = s.Locked;
        Opacity = s.Opacity;
        Scale = s.Scale;
        Width = s.Width;
        MaxItems = s.MaxItems;
        _syncing = false;
    }

    [ObservableProperty]
    private bool _visible;

    [ObservableProperty]
    private bool _locked;

    [ObservableProperty]
    private double _opacity;

    [ObservableProperty]
    private double _scale;

    [ObservableProperty]
    private double _width;

    [ObservableProperty]
    private int _maxItems;

    public string OpacityLabel => $"{Opacity:P0}";
    public string ScaleLabel => $"{Scale:0.00}×";
    public string WidthLabel => $"{Width:0}px";
    public string MaxItemsLabel => MaxItems.ToString();

    partial void OnVisibleChanged(bool value)
    {
        if (_syncing)
            return;
        if (value)
            _controller.Show(_kind);
        else
            _controller.Hide(_kind);
    }

    partial void OnLockedChanged(bool value)
    {
        if (!_syncing)
            _controller.SetLocked(_kind, value);
    }

    partial void OnOpacityChanged(double value)
    {
        OnPropertyChanged(nameof(OpacityLabel));
        if (!_syncing)
            _controller.Update(_kind, s => s with { Opacity = Math.Clamp(value, 0.3, 1) });
    }

    partial void OnScaleChanged(double value)
    {
        OnPropertyChanged(nameof(ScaleLabel));
        if (!_syncing)
            _controller.Update(_kind, s => s with { Scale = Math.Clamp(value, 0.7, 1.6) });
    }

    partial void OnWidthChanged(double value)
    {
        OnPropertyChanged(nameof(WidthLabel));
        if (!_syncing)
            _controller.Update(_kind, s => s with { Width = Math.Clamp(value, 200, 480) });
    }

    partial void OnMaxItemsChanged(int value)
    {
        OnPropertyChanged(nameof(MaxItemsLabel));
        if (!_syncing)
            _controller.Update(_kind, s => s with { MaxItems = Math.Clamp(value, 3, 25) });
    }
}

/// <summary>The Overlays page: the mini parse meter and both timer panels,
/// each fully configurable with instant visual feedback.</summary>
public sealed partial class OverlaysViewModel : ObservableObject
{
    private readonly SourceManager _manager;

    public OverlayConfigVm MiniParse { get; }
    public OverlayConfigVm TimerA { get; }
    public OverlayConfigVm TimerB { get; }

    public IReadOnlyList<string> MetricChoices { get; } = ["DPS", "HPS", "Tanking"];

    [ObservableProperty]
    private int _metricIndex;

    public OverlaysViewModel(SourceManager manager, OverlayController overlay)
    {
        _manager = manager;
        MiniParse = new OverlayConfigVm(overlay, OverlayKind.MiniParse, "Mini parse", "rows");
        TimerA = new OverlayConfigVm(overlay, OverlayKind.TimerA, "Timer panel A", "bars");
        TimerB = new OverlayConfigVm(overlay, OverlayKind.TimerB, "Timer panel B", "bars");
        _metricIndex = Math.Max(0, MetricChoices.ToList().IndexOf(manager.Settings.MiniParseMetric));
    }

    partial void OnMetricIndexChanged(int value)
    {
        _manager.Settings = _manager.Settings with
        {
            MiniParseMetric = MetricChoices[Math.Clamp(value, 0, MetricChoices.Count - 1)],
        };
        _manager.Settings.Save();
    }
}
