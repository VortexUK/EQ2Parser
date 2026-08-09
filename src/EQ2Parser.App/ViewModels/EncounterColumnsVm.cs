using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Localization;
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
/// Configurable "View Options" for one grid: which columns show. The name
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

/// <summary>The app's configurable grids: the view-options catalogue.</summary>
public static class ColumnSets
{
    /// <summary>Encounter view: the main combatant grid. The classic column
    /// set is core (its stats are computed every tick regardless); anything
    /// else triggers the extended swing-bucket walk.</summary>
    public static ColumnSetVm Encounter(SourceManager manager) => new(
        [
            new("Class", Loc.Get("Cols_Class"), true),
            new("Time", Loc.Get("Cols_Time"), true),
            new("Damage", Loc.Get("Cols_Damage"), true),
            new("Percent", Loc.Get("Cols_Percent"), true),
            new("Dps", Loc.Get("Cols_Dps"), true),
            new("Hps", Loc.Get("Cols_Hps"), true),
            new("Heals", Loc.Get("Cols_Heals"), false),
            new("CritHeals", Loc.Get("Cols_CritHeals"), false),
            new("Cures", Loc.Get("Cols_Cures"), false),
            new("PowerDrain", Loc.Get("Cols_PowerDrain"), false),
            new("PowerRep", Loc.Get("Cols_PowerRep"), false),
            new("Swings", Loc.Get("Cols_Swings"), false),
            new("Hits", Loc.Get("Cols_Hits"), false),
            new("Crits", Loc.Get("Cols_Crits"), false),
            new("Misses", Loc.Get("Cols_Misses"), false),
            new("Avoids", Loc.Get("Cols_Avoids"), false),
            new("ToHit", Loc.Get("Cols_ToHit"), false),
            new("CritPct", Loc.Get("Cols_CritPct"), false),
            new("Taken", Loc.Get("Cols_Taken"), true),
            new("HealsTaken", Loc.Get("Cols_HealsTaken"), false),
            new("Deaths", Loc.Get("Cols_Deaths"), true),
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
            new("Time", Loc.Get("Cols_Time"), true),
            new("Result", Loc.Get("Cols_Result"), true),
            new("Crit", Loc.Get("Cols_Crit"), true),
            new("Special", Loc.Get("Cols_Special"), true),
            new("Type", Loc.Get("Cols_Type"), true),
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
            new("Damage", Loc.Get("Cols_Damage"), true),
            new("Dps", Loc.Get("Cols_Dps"), true),
            new("Types", Loc.Get("Cols_Type"), true),
            new("Source", Loc.Get("Cols_Source"), true),
            new("Swings", Loc.Get("Cols_Swings"), true),
            new("Freq", Loc.Get("Cols_Freq"), true),
            new("Hits", Loc.Get("Cols_Hits"), true),
            new("CritPct", Loc.Get("Cols_CritPct"), true),
            new("Avg", Loc.Get("Cols_Avg"), true),
            new("Max", Loc.Get("Cols_Max"), true),
            new("Percent", Loc.Get("Cols_Percent"), true),
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
