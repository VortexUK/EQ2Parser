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
    private readonly ObservableCollection<TimerBarRow> _radials = [];

    public TimerPanelContent(SourceManager manager, int panel)
    {
        _manager = manager;
        _panel = panel;
        InitializeComponent();
        Bars.ItemsSource = _bars;
        Radials.ItemsSource = _radials;
    }

    public bool Refresh(OverlayWindowSettings settings)
    {
        var snapshot = _manager.SpellTimers.Snapshot(DateTimeOffset.Now, _panel);
        var radials = snapshot.Where(b => b.Radial).Take(settings.MaxItems).ToList();
        var bars = snapshot.Where(b => !b.Radial).Take(settings.MaxItems).ToList();
        Sync(_radials, radials);
        Sync(_bars, bars);
        var total = radials.Count + bars.Count;
        EmptyHint.Visibility = total == 0 && !settings.Locked ? Visibility.Visible : Visibility.Collapsed;
        return total > 0;
    }

    private static void Sync(ObservableCollection<TimerBarRow> rows, List<Services.TimerBarSnapshot> bars)
    {
        while (rows.Count > bars.Count)
            rows.RemoveAt(rows.Count - 1);
        while (rows.Count < bars.Count)
            rows.Add(new TimerBarRow());
        for (var i = 0; i < bars.Count; i++)
            TimersViewModel.ApplyBar(rows[i], bars[i]);
    }
}
