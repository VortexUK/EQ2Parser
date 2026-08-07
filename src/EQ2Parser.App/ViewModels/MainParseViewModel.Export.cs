using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Localization;
using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Export;

namespace EQ2Parser.App.ViewModels;

/// <summary>Discord-friendly clipboard export: a fenced monospace table
/// (summary line, header row, aligned data rows) with a user-configurable
/// column set. "Copy for Discord" uses the saved columns; "Custom export…"
/// opens the picker with a live preview.</summary>
public sealed partial class MainParseViewModel
{
    /// <summary>One selectable export column: settings key, checkbox label,
    /// table header, alignment, and the cell renderer.</summary>
    public sealed record ExportColumnDef(
        string Key, string Label, string Header, bool Right, Func<ExportRow, string> Value);

    /// <summary>Raw per-ally values, computed straight from the fight —
    /// independent of which grid columns happen to be visible.</summary>
    public sealed record ExportRow(
        string Name, string Cls, double Seconds, long Damage, double Share,
        double Dps, double Hps, long Taken, int Deaths,
        long Healed, int CritHeals, int Cures, long PowerDrain, long PowerRep,
        int Swings, int Hits, int Crits, int Misses, int Avoids, long HealsTaken);

    /// <summary>Every offerable column, in display order. Name is always
    /// the first column and not part of the selectable set.</summary>
    public static IReadOnlyList<ExportColumnDef> ExportColumnDefs { get; } =
    [
        new("Class", Loc.Get("Cols_Class"), Loc.Get("Main_ColCLASS"), Right: false, r => r.Cls),
        new("Time", Loc.Get("Cols_Time"), Loc.Get("Main_ColTIME"), Right: true, r => FmtSpan(TimeSpan.FromSeconds(r.Seconds))),
        new("Damage", Loc.Get("Cols_Damage"), Loc.Get("Main_ColDAMAGE"), Right: true, r => CombatantRow.Compact(r.Damage)),
        new("Percent", Loc.Get("Cols_Percent"), "%", Right: true, r => $"{r.Share:P1}"),
        new("Dps", Loc.Get("Cols_Dps"), Loc.Get("Main_ColENCDPS"), Right: true, r => CombatantRow.Compact(r.Dps)),
        new("Hps", Loc.Get("Cols_Hps"), Loc.Get("Main_ColENCHPS"), Right: true, r => CombatantRow.Compact(r.Hps)),
        new("Heals", Loc.Get("Cols_Heals"), Loc.Get("Main_ColHEALS"), Right: true, r => CombatantRow.Compact(r.Healed)),
        new("CritHeals", Loc.Get("Cols_CritHeals"), Loc.Get("Main_ColCRITHEAL"), Right: true, r => r.CritHeals.ToString()),
        new("Cures", Loc.Get("Cols_Cures"), Loc.Get("Main_ColCURES"), Right: true, r => r.Cures.ToString()),
        new("PowerDrain", Loc.Get("Cols_PowerDrain"), Loc.Get("Main_ColPWRDRN"), Right: true, r => CombatantRow.Compact(r.PowerDrain)),
        new("PowerRep", Loc.Get("Cols_PowerRep"), Loc.Get("Main_ColPWRREP"), Right: true, r => CombatantRow.Compact(r.PowerRep)),
        new("Swings", Loc.Get("Cols_Swings"), Loc.Get("Main_ColSWINGS"), Right: true, r => r.Swings.ToString()),
        new("Hits", Loc.Get("Cols_Hits"), Loc.Get("Main_ColHITS"), Right: true, r => r.Hits.ToString()),
        new("Crits", Loc.Get("Cols_Crits"), Loc.Get("Main_ColCRITS"), Right: true, r => r.Crits.ToString()),
        new("Misses", Loc.Get("Cols_Misses"), Loc.Get("Main_ColMISS"), Right: true, r => r.Misses.ToString()),
        new("Avoids", Loc.Get("Cols_Avoids"), Loc.Get("Main_ColAVOID"), Right: true, r => r.Avoids.ToString()),
        new("ToHit", Loc.Get("Cols_ToHit"), Loc.Get("Main_ColTOHITPCT"), Right: true, r => r.Swings > 0 ? $"{(double)r.Hits / r.Swings:P0}" : ""),
        new("CritPct", Loc.Get("Cols_CritPct"), Loc.Get("Main_ColCRITPCT"), Right: true, r => r.Hits > 0 ? $"{(double)r.Crits / r.Hits:P0}" : ""),
        new("Taken", Loc.Get("Cols_Taken"), Loc.Get("Main_ColTAKEN"), Right: true, r => CombatantRow.Compact(r.Taken)),
        new("HealsTaken", Loc.Get("Cols_HealsTaken"), Loc.Get("Main_ColHTAKEN"), Right: true, r => CombatantRow.Compact(r.HealsTaken)),
        new("Deaths", Loc.Get("Cols_Deaths"), Loc.Get("Main_ColDEATHS"), Right: true, r => r.Deaths.ToString()),
    ];

    private static readonly string[] DefaultExportKeys = ["Class", "Dps", "Hps", "Percent", "Deaths"];

    /// <summary>The persisted export column selection (falls back to the
    /// default set; unknown saved keys are dropped).</summary>
    public IReadOnlyList<string> ExportColumnKeys =>
        (manager.Settings.ExportColumns ?? [.. DefaultExportKeys])
            .Where(k => ExportColumnDefs.Any(d => d.Key == k)).ToArray();

    public void SaveExportColumns(IReadOnlyList<string> keys)
    {
        manager.Settings = manager.Settings with { ExportColumns = [.. keys] };
        manager.Settings.Save();
    }

    /// <summary>Build the Discord table for a tree node with the given
    /// column selection. Null when the node has nothing to export.</summary>
    public string? BuildDiscordExport(ParseNode? node, IReadOnlyList<string> keys)
    {
        if (node is null)
            return null;
        lock (manager.Sync)
        {
            return node switch
            {
                { GroupFights: { Count: > 0 } group } => FightsTable(node.Title, group),
                { Fight: CorrelatedEncounter fight } => CombatantTable(fight, keys),
                { Fight: AggregateFights aggregate } => FightsTable($"{aggregate.Zone} — {aggregate.Label}", aggregate.Fights),
                { Fight: LiveFollow } => LiveTable(keys),
                _ => null,
            };
        }
    }

    [RelayCommand]
    private void CopyDiscord(ParseNode? node) =>
        SetClipboard(BuildDiscordExport(node, ExportColumnKeys));

    [RelayCommand]
    private void OpenExport(ParseNode? node)
    {
        if (node is null)
            return;
        new Views.ExportWindow(this, node) { Owner = System.Windows.Application.Current?.MainWindow }
            .ShowDialog();
    }

    private string CombatantTable(CorrelatedEncounter fight, IReadOnlyList<string> keys)
    {
        var tags = manager.Classifier.Classify(fight.Primary);
        var seconds = Math.Max(1, fight.Duration.TotalSeconds);
        var allies = fight.MergedCombatants
            .Where(kv => fight.MergedAllyKeys.Contains(kv.Key)
                && tags.TryGetValue(kv.Key, out var tag) && tag.Kind == CombatantKind.Player)
            .Select(kv => (kv.Value.Combatant, Cls: tags[kv.Key].Class.ClassName ?? ""));
        var summary = Loc.Format("Export_FightSummary",
            fight.Title, FmtSpan(fight.Duration), CombatantRow.Compact(fight.EncDps));
        return RenderTable(summary, allies, seconds, keys);
    }

    private string? LiveTable(IReadOnlyList<string> keys)
    {
        foreach (var source in manager.Sources)
        {
            if (source.Engine.ActiveEncounter is not { } encounter)
                continue;
            var tags = manager.Classifier.Classify(encounter);
            var seconds = Math.Max(1, encounter.Duration.TotalSeconds);
            var allies = encounter.GetAllies()
                .Select(a => (Combatant: a, Cls: tags.GetValueOrDefault(a.Key)?.Class.ClassName ?? ""));
            var summary = Loc.Format("Export_FightSummary",
                encounter.Title, FmtSpan(encounter.Duration), CombatantRow.Compact(encounter.EncDps));
            return RenderTable(summary, allies, seconds, keys);
        }
        return null;
    }

    private string RenderTable(
        string summary, IEnumerable<(Combatant Combatant, string Cls)> allies,
        double seconds, IReadOnlyList<string> keys)
    {
        List<ExportRow> rows = [];
        long total = 0;
        foreach (var (combatant, cls) in allies)
        {
            if (combatant.Damage <= 0 && combatant.Healed <= 0 && combatant.DamageTaken <= 0)
                continue;
            total += combatant.Damage;
            var damage = combatant.OutgoingBuckets.GetValueOrDefault(BucketConfig.OutgoingDamage)
                ?.Abilities.GetValueOrDefault(Bucket.AllAbility);
            var heals = combatant.OutgoingBuckets.GetValueOrDefault(BucketConfig.HealedOut)
                ?.Abilities.GetValueOrDefault(Bucket.AllAbility);
            rows.Add(new ExportRow(
                combatant.Name, cls, combatant.Duration.TotalSeconds,
                combatant.Damage, Share: 0,
                combatant.Damage / seconds, combatant.Healed / seconds,
                combatant.DamageTaken, combatant.Deaths,
                combatant.Healed, heals?.CritHits ?? 0, combatant.CureDispels,
                combatant.PowerDamage, combatant.PowerReplenish,
                damage?.SwingCount ?? 0, damage?.Hits ?? 0, damage?.CritHits ?? 0,
                damage?.Misses ?? 0, damage?.Avoids ?? 0, combatant.HealsTaken));
        }
        var ordered = rows
            .Select(r => r with { Share = total > 0 ? (double)r.Damage / total : 0 })
            .OrderByDescending(r => r.Damage)
            .ToList();

        var defs = keys.Select(k => ExportColumnDefs.First(d => d.Key == k)).ToList();
        List<ExportColumn> columns = [new(Loc.Get("Main_ColNAME"), RightAlign: false)];
        columns.AddRange(defs.Select(d => new ExportColumn(d.Header, d.Right)));
        var cells = ordered
            .Select(r => new[] { r.Name }.Concat(defs.Select(d => d.Value(r))).ToArray())
            .ToList();
        return TableExport.BuildDiscord(summary, columns, cells, Loc.Get("Export_MoreRows"));
    }

    /// <summary>Zone/rollup nodes: one row per fight instead of combatants.</summary>
    private string FightsTable(string label, IReadOnlyList<CorrelatedEncounter> fights)
    {
        List<ExportColumn> columns =
        [
            new(Loc.Get("Main_ColENCOUNTER"), RightAlign: false),
            new(Loc.Get("Main_ColTIME"), RightAlign: true),
            new(Loc.Get("Main_ColENCDPS"), RightAlign: true),
        ];
        var rows = fights
            .Select(f => new[] { f.Title, FmtSpan(f.Duration), CombatantRow.Compact(f.EncDps) })
            .ToList();
        var summary = Loc.Format("Export_ZoneSummary", label, fights.Count, FmtSpan(SumDuration(fights)));
        return TableExport.BuildDiscord(summary, columns, rows, Loc.Get("Export_MoreRows"));
    }
}
