using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EQ2Parser.App.Services;
using EQ2Parser.App.ViewModels;
using EQ2Parser.Core.Analysis;

namespace EQ2Parser.App.Views;

/// <summary>One meter row, updated in place so bars glide rather than churn.</summary>
public sealed partial class MiniParseRowVm : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _classText = "";

    [ObservableProperty]
    private string _deathsText = "";

    [ObservableProperty]
    private string _valueText = "";

    [ObservableProperty]
    private string _shareText = "";

    [ObservableProperty]
    private double _fraction;

    [ObservableProperty]
    private Brush _nameBrush = Brushes.White;

    [ObservableProperty]
    private Brush _barBrush = Brushes.SteelBlue;
}

public partial class MiniParseContent : IOverlayContent
{
    private const double RowHeight = 22;

    private readonly SourceManager _manager;
    private readonly MiniParseSnapshot _snapshot;
    private readonly OverlayFadeGate _fade = new();
    private readonly string _metric;
    private readonly ObservableCollection<MiniParseRowVm> _rows = [];

    public MiniParseContent(SourceManager manager, string metric)
    {
        _manager = manager;
        _snapshot = new MiniParseSnapshot(manager);
        _metric = metric;
        InitializeComponent();
        RowsHost.ItemsSource = _rows;
    }

    public bool Refresh(OverlayWindowSettings settings)
    {
        // Rows fill whatever height the user resized the window to — drag
        // it taller and the whole raid fits.
        var available = RowsHost.ActualHeight;
        var fit = available > RowHeight ? (int)(available / RowHeight) : 10;
        var data = _snapshot.Build(Math.Clamp(fit, 1, 30), _metric);
        FightTitle.Text = data.Title;
        DurationText.Text = data.DurationLabel;
        MetricText.Text = data.MetricLabel;
        RaidValueText.Text = data.RaidValue > 0 ? CombatantRow.Compact(data.RaidValue) : "";

        while (_rows.Count > data.Rows.Count)
            _rows.RemoveAt(_rows.Count - 1);
        while (_rows.Count < data.Rows.Count)
            _rows.Add(new MiniParseRowVm());
        // Column toggles: an off column renders as empty text, and its
        // Auto grid column collapses to nothing. Null = all on (default).
        var columns = settings.MeterColumns;
        bool On(string key) => columns is null || columns.Contains(key);
        var showClass = On("Class");
        var showDeaths = On("Deaths");
        var showValue = On("Value");
        var showShare = On("Share");
        for (var i = 0; i < data.Rows.Count; i++)
        {
            var row = data.Rows[i];
            var vm = _rows[i];
            vm.Name = row.Name;
            vm.ClassText = showClass && row.ClassName is { Length: > 0 } cls ? $" <{cls}>" : "";
            vm.DeathsText = showDeaths && row.Deaths > 0 ? $"☠{row.Deaths}" : "";
            vm.ValueText = showValue ? CombatantRow.Compact(row.Value) : "";
            vm.ShareText = showShare ? $"{row.Fraction:P0}" : "";
            vm.Fraction = Math.Clamp(row.Fraction, 0, 1);
            var archetype = (SolidColorBrush)ClassColors.For(row.ClassName);
            vm.BarBrush = archetype;
            vm.NameBrush = NameBrushFor(archetype);
        }
        EmptyHint.Visibility = data.Rows.Count == 0 && !settings.Locked ? Visibility.Visible : Visibility.Collapsed;
        // Post-fight fade: report "nothing to show" once combat has been
        // over for the configured hold — the shell fades a LOCKED overlay
        // to zero on that signal (unlocked ones stay put for dragging).
        // Option off → hold 0 → the gate never hides.
        var hold = _manager.Settings.MiniParseFadeEnabled
            ? Math.Max(1, _manager.Settings.MiniParseFadeSeconds)
            : 0;
        var faded = _fade.ShouldHide(data.InCombat, hold, DateTimeOffset.Now);
        return data.Rows.Count > 0 && !faded;
    }

    /// <summary>Name in a lightened archetype tone so it stays readable on
    /// the dark glass while still reading as the class. Cached per colour —
    /// a fresh brush per row per 150ms tick forced PropertyChanged + effect
    /// re-renders even when nothing changed.</summary>
    private static readonly Dictionary<Color, SolidColorBrush> NameBrushes = [];

    private static SolidColorBrush NameBrushFor(SolidColorBrush archetype)
    {
        var c = archetype.Color;
        if (NameBrushes.TryGetValue(c, out var cached))
            return cached;
        var brush = new SolidColorBrush(Color.FromRgb(
            (byte)(c.R + (255 - c.R) * 0.35), (byte)(c.G + (255 - c.G) * 0.35), (byte)(c.B + (255 - c.B) * 0.35)));
        brush.Freeze();
        return NameBrushes[c] = brush;
    }
}
