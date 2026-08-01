using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EQ2Parser.App.Services;
using EQ2Parser.App.ViewModels;

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

    private readonly MiniParseSnapshot _snapshot;
    private readonly string _metric;
    private readonly ObservableCollection<MiniParseRowVm> _rows = [];

    public MiniParseContent(SourceManager manager, string metric)
    {
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
        for (var i = 0; i < data.Rows.Count; i++)
        {
            var row = data.Rows[i];
            var vm = _rows[i];
            vm.Name = row.Name;
            vm.ClassText = row.ClassName is { Length: > 0 } cls ? $" <{cls}>" : "";
            vm.DeathsText = row.Deaths > 0 ? $"☠{row.Deaths}" : "";
            vm.ValueText = CombatantRow.Compact(row.Value);
            vm.ShareText = $"{row.Fraction:P0}";
            vm.Fraction = Math.Clamp(row.Fraction, 0, 1);
            var archetype = (SolidColorBrush)ClassColors.For(row.ClassName);
            vm.BarBrush = archetype;
            // Name in a lightened archetype tone so it stays readable on the
            // dark glass while still reading as the class.
            var c = archetype.Color;
            var name = new SolidColorBrush(Color.FromRgb(
                (byte)(c.R + (255 - c.R) * 0.35), (byte)(c.G + (255 - c.G) * 0.35), (byte)(c.B + (255 - c.B) * 0.35)));
            name.Freeze();
            vm.NameBrush = name;
        }
        EmptyHint.Visibility = data.Rows.Count == 0 && !settings.Locked ? Visibility.Visible : Visibility.Collapsed;
        return data.Rows.Count > 0;
    }
}
