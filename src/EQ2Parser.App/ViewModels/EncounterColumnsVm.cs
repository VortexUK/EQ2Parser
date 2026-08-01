using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.ViewModels;

/// <summary>One togglable column of the encounter grid. The header button,
/// the row cells, and both grids' ColumnDefinitions all bind to
/// <see cref="Visible"/>, so flipping it collapses the column everywhere.</summary>
public sealed partial class ColumnToggle(string key, string label, bool defaultVisible) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public bool DefaultVisible { get; } = defaultVisible;

    [ObservableProperty]
    private bool _visible = defaultVisible;

    /// <summary>Owner-installed persistence hook.</summary>
    internal Action? Changed { get; set; }

    partial void OnVisibleChanged(bool value) => Changed?.Invoke();
}

/// <summary>
/// ACT-style "Encounter View Options": which columns the combatant grid
/// shows. NAME is always on; everything else toggles and persists in
/// settings. The grid snapshot only walks the swing buckets for the
/// extended stats (swings/hits/crits/heal detail) while at least one of
/// those columns is on — the default view costs nothing extra.
/// </summary>
public sealed partial class EncounterColumnsVm : ObservableObject
{
    /// <summary>The classic column set whose stats every snapshot already
    /// computes; anything else needs the extended bucket walk.</summary>
    private static readonly HashSet<string> CoreKeys =
        ["Class", "Time", "Damage", "Percent", "Dps", "Hps", "Taken", "Deaths"];

    private readonly SourceManager _manager;
    private bool _applying;

    public IReadOnlyList<ColumnToggle> Toggles { get; }

    /// <summary>Key-addressable view for XAML indexer bindings
    /// (<c>ByKey[Swings].Visible</c>).</summary>
    public Dictionary<string, ColumnToggle> ByKey { get; }

    public EncounterColumnsVm(SourceManager manager)
    {
        _manager = manager;
        Toggles =
        [
            new("Class", "Class", true),
            new("Time", "Time", true),
            new("Damage", "Damage", true),
            new("Percent", "% share", true),
            new("Dps", "EncDPS", true),
            new("Hps", "EncHPS", true),
            new("Heals", "Heals (total)", false),
            new("CritHeals", "Crit heals", false),
            new("Cures", "Cures", false),
            new("PowerDrain", "Power drain", false),
            new("PowerRep", "Power replenish", false),
            new("Swings", "Swings", false),
            new("Hits", "Hits", false),
            new("Crits", "Crit hits", false),
            new("Misses", "Misses", false),
            new("Avoids", "Avoids", false),
            new("ToHit", "ToHit %", false),
            new("CritPct", "Crit %", false),
            new("Taken", "Damage taken", true),
            new("HealsTaken", "Healing taken", false),
            new("Deaths", "Deaths", true),
        ];
        ByKey = Toggles.ToDictionary(t => t.Key, StringComparer.Ordinal);
        if (manager.Settings.EncounterColumns is { } saved)
        {
            var visible = new HashSet<string>(saved, StringComparer.Ordinal);
            foreach (var toggle in Toggles)
                toggle.Visible = visible.Contains(toggle.Key);
        }
        foreach (var toggle in Toggles)
            toggle.Changed = Persist;
    }

    /// <summary>Any column beyond the classic set is on.</summary>
    public bool Extended
    {
        get
        {
            foreach (var toggle in Toggles)
            {
                if (toggle.Visible && !CoreKeys.Contains(toggle.Key))
                    return true;
            }
            return false;
        }
    }

    private void Persist()
    {
        if (_applying)
            return;
        _manager.Settings = _manager.Settings with
        {
            EncounterColumns = [.. Toggles.Where(t => t.Visible).Select(t => t.Key)],
        };
        _manager.Settings.Save();
    }

    [RelayCommand]
    private void Reset()
    {
        _applying = true;
        foreach (var toggle in Toggles)
            toggle.Visible = toggle.DefaultVisible;
        _applying = false;
        // Null = "never customised" — future default changes apply cleanly.
        _manager.Settings = _manager.Settings with { EncounterColumns = null };
        _manager.Settings.Save();
    }
}
