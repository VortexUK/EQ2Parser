using System.Collections.ObjectModel;
using System.Windows;
using EQ2Parser.App.Services;
using EQ2Parser.App.ViewModels;

namespace EQ2Parser.App.Views;

public partial class TimerPanelContent : IOverlayContent
{
    private readonly SourceManager _manager;
    private readonly int _panel;
    private readonly ObservableCollection<TimerBarRow> _bars = [];

    public TimerPanelContent(SourceManager manager, int panel)
    {
        _manager = manager;
        _panel = panel;
        InitializeComponent();
        Bars.ItemsSource = _bars;
    }

    public bool Refresh(OverlayWindowSettings settings)
    {
        var bars = _manager.SpellTimers.Snapshot(DateTimeOffset.Now, _panel);
        if (bars.Count > settings.MaxItems)
            bars.RemoveRange(settings.MaxItems, bars.Count - settings.MaxItems);
        while (_bars.Count > bars.Count)
            _bars.RemoveAt(_bars.Count - 1);
        while (_bars.Count < bars.Count)
            _bars.Add(new TimerBarRow());
        for (var i = 0; i < bars.Count; i++)
            TimersViewModel.ApplyBar(_bars[i], bars[i]);
        EmptyHint.Visibility = bars.Count == 0 && !settings.Locked ? Visibility.Visible : Visibility.Collapsed;
        return bars.Count > 0;
    }
}
