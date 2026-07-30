using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Analysis;

namespace EQ2Parser.App.ViewModels;

/// <summary>One row of the combatant grid (ally or enemy section). Updated
/// in place each refresh tick so WPF only repaints changed cells. Raw
/// numeric fields drive sorting; formatted strings drive display.</summary>
public sealed partial class CombatantRow : ObservableObject
{
    public required string Key { get; set; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _className = "";

    [ObservableProperty]
    private Brush _classBrush = ClassColors.Neutral;

    [ObservableProperty]
    private CombatantKind _kind;

    [ObservableProperty]
    private bool _isPet;

    [ObservableProperty]
    private string _duration = "";

    [ObservableProperty]
    private string _damage = "";

    [ObservableProperty]
    private string _percent = "";

    [ObservableProperty]
    private string _dps = "";

    [ObservableProperty]
    private string _hps = "";

    [ObservableProperty]
    private string _taken = "";

    [ObservableProperty]
    private string _deaths = "";

    /// <summary>0..1 share of the top row's damage — drives the row bar.</summary>
    [ObservableProperty]
    private double _barFraction;

    public static string Compact(double value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000:0.##}B",
        >= 1_000_000 => $"{value / 1_000_000:0.##}M",
        >= 10_000 => $"{value / 1_000:0.#}K",
        _ => $"{value:N0}",
    };
}
