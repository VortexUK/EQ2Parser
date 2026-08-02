using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.ViewModels;

/// <summary>One togglable column of a grid. The header cell, the row cells,
/// and both grids' ColumnDefinitions all bind to <see cref="Visible"/>, so
/// flipping it collapses the column everywhere.</summary>
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
/// ACT-style "View Options" for one grid: which columns show. The name
/// column is always on; everything else toggles and persists in settings
/// (null = never customised, so future default changes apply cleanly).
/// </summary>
public sealed partial class ColumnSetVm : ObservableObject
{
    private readonly HashSet<string> _coreKeys;
    private readonly Action<List<string>?> _save;
    private bool _applying;

    public IReadOnlyList<ColumnToggle> Toggles { get; }

    /// <summary>Key-addressable view for XAML indexer bindings
    /// (<c>ByKey[Swings].Visible</c>).</summary>
    public Dictionary<string, ColumnToggle> ByKey { get; }

    public ColumnSetVm(
        IReadOnlyList<ColumnToggle> toggles, HashSet<string> coreKeys,
        List<string>? saved, Action<List<string>?> save)
    {
        Toggles = toggles;
        _coreKeys = coreKeys;
        _save = save;
        ByKey = toggles.ToDictionary(t => t.Key, StringComparer.Ordinal);
        if (saved is { } keys)
        {
            var visible = new HashSet<string>(keys, StringComparer.Ordinal);
            foreach (var toggle in Toggles)
                toggle.Visible = visible.Contains(toggle.Key);
        }
        foreach (var toggle in Toggles)
            toggle.Changed = Persist;
    }

    /// <summary>Any column beyond the always-computed core set is on — the
    /// grid snapshot only gathers the pricier stats while this holds.</summary>
    public bool Extended
    {
        get
        {
            foreach (var toggle in Toggles)
            {
                if (toggle.Visible && !_coreKeys.Contains(toggle.Key))
                    return true;
            }
            return false;
        }
    }

    private void Persist()
    {
        if (_applying)
            return;
        _save([.. Toggles.Where(t => t.Visible).Select(t => t.Key)]);
    }

    [RelayCommand]
    private void Reset()
    {
        _applying = true;
        foreach (var toggle in Toggles)
            toggle.Visible = toggle.DefaultVisible;
        _applying = false;
        _save(null);
    }
}

/// <summary>The app's configurable grids, ACT's view-options catalogue.</summary>
public static class ColumnSets
{
    /// <summary>Encounter view: the main combatant grid. The classic column
    /// set is core (its stats are computed every tick regardless); anything
    /// else triggers the extended swing-bucket walk.</summary>
    public static ColumnSetVm Encounter(SourceManager manager) => new(
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
        ],
        ["Class", "Time", "Damage", "Percent", "Dps", "Hps", "Taken", "Deaths"],
        manager.Settings.EncounterColumns,
        keys =>
        {
            manager.Settings = manager.Settings with { EncounterColumns = keys };
            manager.Settings.Save();
        });

    /// <summary>AttackType view: the swing log (drill depth 3). The target
    /// column is the stretch column and always on.</summary>
    public static ColumnSetVm Swing(SourceManager manager)
    {
        List<ColumnToggle> toggles =
        [
            new("Time", "Time", true),
            new("Result", "Result", true),
            new("Crit", "Crit", true),
            new("Special", "Special", true),
            new("Type", "Type", true),
        ];
        return new(
            toggles,
            [.. toggles.Select(t => t.Key)],
            manager.Settings.AttackTypeColumns,
            keys =>
            {
                manager.Settings = manager.Settings with { AttackTypeColumns = keys };
                manager.Settings.Save();
            });
    }

    /// <summary>Combatant view: the drill-down bucket/ability table. Every
    /// stat is already accumulated, so all columns are core.</summary>
    public static ColumnSetVm Drill(SourceManager manager)
    {
        List<ColumnToggle> toggles =
        [
            new("Damage", "Damage", true),
            new("Dps", "EncDPS", true),
            new("Types", "Type", true),
            new("Source", "Source", true),
            new("Swings", "Swings", true),
            new("Freq", "Frequency", true),
            new("Hits", "Hits", true),
            new("CritPct", "Crit %", true),
            new("Avg", "Average", true),
            new("Max", "Max hit", true),
            new("Percent", "% share", true),
        ];
        return new(
            toggles,
            [.. toggles.Select(t => t.Key)],
            manager.Settings.CombatantColumns,
            keys =>
            {
                manager.Settings = manager.Settings with { CombatantColumns = keys };
                manager.Settings.Save();
            });
    }
}
