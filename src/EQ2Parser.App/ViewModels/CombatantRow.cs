using CommunityToolkit.Mvvm.ComponentModel;
using EQ2Parser.Core.Analysis;

namespace EQ2Parser.App.ViewModels;

/// <summary>One row of the damage/heal meter. Updated in place each refresh
/// tick so WPF only repaints changed cells.</summary>
public sealed partial class CombatantRow : ObservableObject
{
    public required string Key { get; init; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _className = "";

    [ObservableProperty]
    private CombatantKind _kind;

    [ObservableProperty]
    private string _dps = "";

    [ObservableProperty]
    private string _damage = "";

    [ObservableProperty]
    private string _percent = "";

    /// <summary>0..1 share of the top row's value — drives the meter bar.</summary>
    [ObservableProperty]
    private double _barFraction;

    [ObservableProperty]
    private bool _isPet;

    public static string Compact(double value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000:0.##}B",
        >= 1_000_000 => $"{value / 1_000_000:0.##}M",
        >= 10_000 => $"{value / 1_000:0.#}K",
        _ => $"{value:N0}",
    };
}
