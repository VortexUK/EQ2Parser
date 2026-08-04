using CommunityToolkit.Mvvm.Input;
using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;
using LiveChartsCore;
using SkiaSharp;

namespace EQ2Parser.App.ViewModels;

/// <summary>The Sources report: where each ally's damage came from —
/// class kit / granted raid buff / item proc / auto-attack — at fight scope
/// (per-ally split table) and combatant scope (per-ability listing grouped
/// by source). The raid/item footers double as the curation feed for
/// source_overrides.json: a class ability showing under ITEM is a mislabel
/// to promote into the overrides file.</summary>
public sealed partial class MainParseViewModel
{
    private static readonly AbilitySource[] SourceOrder =
        [AbilitySource.Class, AbilitySource.Raid, AbilitySource.Item, AbilitySource.System];

    private static readonly SKColor[] SourceSk =
    [
        new(0xC8, 0xA9, 0x6E), // class — gold
        new(0x93, 0xD9, 0xFF), // raid — blue
        new(0x9D, 0x9D, 0x9D), // item — grey
        new(0x8B, 0x90, 0xAB), // system — slate
    ];

    private static System.Windows.Media.Brush SourceBrush(AbilitySource source) => source switch
    {
        AbilitySource.Class => Services.ClassColors.SourceClass,
        AbilitySource.Raid => Services.ClassColors.SourceRaid,
        AbilitySource.Item => Services.ClassColors.SourceItem,
        _ => Services.ClassColors.Neutral,
    };

    /// <summary>"System" is developer jargon — the bucket is auto-attack.</summary>
    private static string SourceLabel(AbilitySource source) =>
        source == AbilitySource.System ? "Auto-attack" : source.ToString();

    /// <summary>Per-target accumulation: damage per source, plus per-ability
    /// rollups for the detail view and the curation footers.</summary>
    private sealed class SourceTally
    {
        public readonly long[] BySource = new long[4];
        public readonly Dictionary<string, (AbilitySource Source, long Damage, int Hits)> ByAbility = new(StringComparer.Ordinal);
        public long Total;
        public string? DetectedClass;
        public bool Undetected;
    }

    private SourceTally AccumulateSources(IEnumerable<Combatant> combatants, string? detectedClass)
    {
        var tally = new SourceTally { DetectedClass = detectedClass, Undetected = detectedClass is null };
        foreach (var combatant in combatants)
        {
            if (combatant.OutgoingBuckets.GetValueOrDefault(BucketConfig.OutgoingDamage) is not { } bucket)
                continue;
            foreach (var (ability, stats) in bucket.Abilities)
            {
                if (ability is Bucket.AllAbility or Combatant.KillingAbility || stats.Damage <= 0)
                    continue;
                var source = manager.Classifier.Identifier.ClassifySource(ability, detectedClass);
                tally.BySource[(int)source] += stats.Damage;
                tally.Total += stats.Damage;
                tally.ByAbility.TryGetValue(ability, out var acc);
                tally.ByAbility[ability] = (source, acc.Damage + stats.Damage, acc.Hits + stats.Hits);
            }
        }
        return tally;
    }

    private Dictionary<string, CombatantTag> SourceReportTags(object? fight) => fight switch
    {
        Encounter e => new(manager.Classifier.Classify(e), StringComparer.Ordinal),
        CorrelatedEncounter m => new(manager.Classifier.Classify(m.Primary), StringComparer.Ordinal),
        AggregateFights a => a.Fights
            .SelectMany(f => manager.Classifier.Classify(f.Primary))
            .GroupBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal),
        _ => new(StringComparer.Ordinal),
    };

    /// <summary>Fight node → per-ally source-split table; combatant row →
    /// per-ability listing grouped by source.</summary>
    [RelayCommand]
    private void SourcesReport(object? parameter)
    {
        RecordReportScope(parameter, 4);
        if (parameter is CombatantRow row)
        {
            CombatantSourcesReport(row);
            return;
        }
        lock (manager.Sync)
        {
            var targets = ReportTargets(parameter, out var context);
            var fightObj = parameter is ParseNode { Fight: { } nodeFight } ? nodeFight : ResolveFight();
            var tags = SourceReportTags(fightObj);

            List<(string Name, SourceTally T)> rows = [];
            foreach (var (name, combatants) in targets
                .GroupBy(t => t.Name)
                .Select(g => (g.Key, g.Select(t => t.C).ToList())))
            {
                var detected = tags.TryGetValue(name.ToUpperInvariant(), out var tag) ? tag.Class.ClassName : null;
                var tally = AccumulateSources(combatants, detected);
                if (tally.Total > 0)
                    rows.Add((name, tally));
            }

            ReportLine(
                ("NAME".PadRight(18), Services.ClassColors.TreeHeader),
                ("CLASS".PadRight(14), Services.ClassColors.TreeHeader),
                ("   TOTAL", Services.ClassColors.TreeHeader),
                ("          CLASS           RAID           ITEM           AUTO", Services.ClassColors.TreeHeader));

            var totals = new long[4];
            long grandTotal = 0;
            static string Cell(long damage, long total) =>
                damage > 0 ? $"{CombatantRow.Compact(damage),7} {100.0 * damage / total,4:F0}%" : "—".PadLeft(13);
            foreach (var (name, tally) in rows.OrderByDescending(r => r.T.Total))
            {
                ReportLine(
                    (name.PadRight(18), Services.ClassColors.TreeText),
                    ((tally.DetectedClass ?? "?").PadRight(14), tally.Undetected ? Services.ClassColors.Neutral : Services.ClassColors.TreeText),
                    ($"{CombatantRow.Compact(tally.Total),8}", Services.ClassColors.TreeText),
                    ("  " + Cell(tally.BySource[0], tally.Total), SourceBrush(AbilitySource.Class)),
                    ("  " + Cell(tally.BySource[1], tally.Total), SourceBrush(AbilitySource.Raid)),
                    ("  " + Cell(tally.BySource[2], tally.Total), SourceBrush(AbilitySource.Item)),
                    ("  " + Cell(tally.BySource[3], tally.Total), SourceBrush(AbilitySource.System)));
                for (var i = 0; i < 4; i++)
                    totals[i] += tally.BySource[i];
                grandTotal += tally.Total;
            }
            if (grandTotal > 0)
            {
                ReportLine(
                    ("TOTAL".PadRight(18), Services.ClassColors.TreeHeader),
                    ("".PadRight(14), Services.ClassColors.TreeText),
                    ($"{CombatantRow.Compact(grandTotal),8}", Services.ClassColors.TreeText),
                    ("  " + Cell(totals[0], grandTotal), SourceBrush(AbilitySource.Class)),
                    ("  " + Cell(totals[1], grandTotal), SourceBrush(AbilitySource.Raid)),
                    ("  " + Cell(totals[2], grandTotal), SourceBrush(AbilitySource.Item)),
                    ("  " + Cell(totals[3], grandTotal), SourceBrush(AbilitySource.System)));
            }

            RenderRaidBuffContributions(rows);

            // Curation footers: what the raid's granted buffs actually did,
            // and the top item-labelled names (mislabels surface here —
            // promote them into source_overrides.json).
            Footer(rows, AbilitySource.Raid, "top raid-granted");
            Footer(rows, AbilitySource.Item, "top item/proc");
            var undetected = rows.Where(r => r.T.Undetected).Select(r => r.Name).ToList();
            if (undetected.Count > 0)
            {
                ReportLine(("", Services.ClassColors.Neutral));
                ReportLine((
                    $"class undetected for {string.Join(", ", undetected)} — their class/raid split is unreliable",
                    Services.ClassColors.Neutral));
            }

            OpenReport($"{context} › sources report");

            if (grandTotal > 0)
            {
                ReportChartTitle1 = "SOURCE SPLIT";
                ReportChartTitle2 = "RAID-GRANTED ABILITIES";
                ReportDonutsVisible = true;
                ReportCartesianVisible = false;
                ReportDonutInner = [.. SourceOrder
                    .Where(s => totals[(int)s] > 0)
                    .Select(ISeries (s) => Ring(SourceLabel(s), totals[(int)s], SourceSk[(int)s], grandTotal, 44))];
                var raidAbilities = rows
                    .SelectMany(r => r.T.ByAbility)
                    .Where(kv => kv.Value.Source == AbilitySource.Raid)
                    .GroupBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(g => (Name: g.Key, Damage: g.Sum(kv => kv.Value.Damage)))
                    .OrderByDescending(t => t.Damage)
                    .Take(8)
                    .ToList();
                var raidTotal = Math.Max(1, totals[(int)AbilitySource.Raid]);
                ReportDonutOuter = [.. raidAbilities
                    .Select((t, i) => Ring(t.Name, t.Damage, ShadeOf(SourceSk[1], i), raidTotal, 44))];
                ReportChartVisible = true;
            }
        }

        void Footer(List<(string Name, SourceTally T)> rows, AbilitySource source, string label)
        {
            var top = rows
                .SelectMany(r => r.T.ByAbility)
                .Where(kv => kv.Value.Source == source)
                .GroupBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(g => (Name: g.Key, Damage: g.Sum(kv => kv.Value.Damage)))
                .OrderByDescending(t => t.Damage)
                .Take(6)
                .ToList();
            if (top.Count == 0)
                return;
            ReportLine(("", Services.ClassColors.Neutral));
            ReportLine(
                ($"{label}: ", Services.ClassColors.TreeHeader),
                (string.Join(" · ", top.Select(t => $"{t.Name} {CombatantRow.Compact(t.Damage)}")), SourceBrush(source)));
        }
    }

    private void CombatantSourcesReport(CombatantRow row)
    {
        lock (manager.Sync)
        {
            var targets = ReportTargets(row, out var context);
            var tags = SourceReportTags(ResolveFight());
            var detected = tags.TryGetValue(row.Key, out var tag) ? tag.Class.ClassName : null;
            var tally = AccumulateSources(targets.Select(t => t.C), detected);

            if (tally.Total == 0)
            {
                ReportLine(("No outgoing damage.", Services.ClassColors.Neutral));
                OpenReport($"{context} › sources report");
                return;
            }

            ReportLine(
                ($"{context} — {detected ?? "class undetected"}", detected is null ? Services.ClassColors.Neutral : Services.ClassColors.TreeText),
                ($"   total {CombatantRow.Compact(tally.Total)}", Services.ClassColors.TreeText),
                (detected is null ? "   (class/raid split unreliable)" : "", Services.ClassColors.Neutral));
            ReportLine(("", Services.ClassColors.Neutral));
            ReportLine(
                ("SOURCE".PadRight(9), Services.ClassColors.TreeHeader),
                ("ABILITY".PadRight(28), Services.ClassColors.TreeHeader),
                ("   DAMAGE   % TOTAL     HITS", Services.ClassColors.TreeHeader));

            foreach (var source in SourceOrder)
            {
                var abilities = tally.ByAbility
                    .Where(kv => kv.Value.Source == source)
                    .OrderByDescending(kv => kv.Value.Damage)
                    .ToList();
                if (abilities.Count == 0)
                    continue;
                var subtotal = abilities.Sum(kv => kv.Value.Damage);
                ReportLine(
                    ((source == AbilitySource.System ? "AUTO" : source.ToString().ToUpperInvariant()).PadRight(9), SourceBrush(source)),
                    ($"{abilities.Count} abilities".PadRight(28), Services.ClassColors.Neutral),
                    ($"{CombatantRow.Compact(subtotal),9}  {100.0 * subtotal / tally.Total,6:F1}%", SourceBrush(source)));
                foreach (var (ability, (_, damage, hits)) in abilities)
                {
                    ReportLine(
                        ("".PadRight(9), Services.ClassColors.TreeText),
                        (ability.PadRight(28), Services.ClassColors.TreeText),
                        ($"{CombatantRow.Compact(damage),9}  {100.0 * damage / tally.Total,6:F1}%  {hits,7:N0}", Services.ClassColors.TreeText));
                }
            }

            RenderGrantedToRaid(row, detected);

            OpenReport($"{context} › sources report");

            ReportChartTitle1 = "SOURCE SPLIT";
            ReportChartTitle2 = "TOP ABILITIES";
            ReportDonutsVisible = true;
            ReportCartesianVisible = false;
            ReportDonutInner = [.. SourceOrder
                .Where(s => tally.BySource[(int)s] > 0)
                .Select(ISeries (s) => Ring(SourceLabel(s), tally.BySource[(int)s], SourceSk[(int)s], tally.Total, 44))];
            ReportDonutOuter = [.. tally.ByAbility
                .OrderByDescending(kv => kv.Value.Damage)
                .Take(10)
                .Select(ISeries (kv) => Ring(kv.Key, kv.Value.Damage, SourceSk[(int)kv.Value.Source], tally.Total, 44))];
            ReportChartVisible = true;
        }
    }

    /// <summary>The facet-2 payoff: raid-granted proc damage credited back
    /// to the class member who granted the buff — the support-class number
    /// no meter ever shows. Inference by detected class (the logs carry no
    /// caster identity for granted buffs — verified empirically); an even
    /// split among same-class granters is flagged, never silently exact.</summary>
    private (IReadOnlyList<RaidBuffAttributor.Credit> Credits, Dictionary<string, string> ClassByAlly)
        ComputeRaidCredits(List<(string Name, SourceTally T)> rows)
    {
        var raidSourced = rows
            .SelectMany(r => r.T.ByAbility)
            .Where(kv => kv.Value.Source == AbilitySource.Raid)
            .GroupBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(g => (Ability: g.Key, Damage: g.Sum(kv => kv.Value.Damage)))
            .OrderByDescending(t => t.Damage)
            .ToList();
        var classByAlly = rows
            .Where(r => r.T.DetectedClass is not null)
            .ToDictionary(r => r.Name, r => r.T.DetectedClass!, StringComparer.OrdinalIgnoreCase);
        var credits = new RaidBuffAttributor(
                manager.Classifier.Identifier.Map, manager.Classifier.Identifier.Overrides)
            .Attribute(raidSourced, classByAlly);
        return (credits, classByAlly);
    }

    private void RenderRaidBuffContributions(List<(string Name, SourceTally T)> rows)
    {
        var (credits, classByAlly) = ComputeRaidCredits(rows);
        if (credits.Count == 0)
            return;
        var byGranter = RaidBuffAttributor.CreditByGranter(credits);

        ReportLine(("", Services.ClassColors.Neutral));
        ReportLine(("RAID-BUFF CONTRIBUTIONS (granted procs credited to the granting class)", Services.ClassColors.TreeHeader));
        foreach (var (granter, credited) in byGranter.OrderByDescending(kv => kv.Value))
        {
            var mine = credits.Where(c => c.Granters.Contains(granter, StringComparer.OrdinalIgnoreCase)).ToList();
            var estimated = mine.Any(c => c.Estimated);
            var from = string.Join(" · ", mine
                .OrderByDescending(c => c.Damage)
                .Take(4)
                .Select(c => $"{c.Ability} {CombatantRow.Compact(c.Damage / Math.Max(1, c.Granters.Count))}"));
            ReportLine(
                (granter.PadRight(18), Services.ClassColors.TreeText),
                ((classByAlly.GetValueOrDefault(granter) ?? "").PadRight(14), Services.ClassColors.TreeText),
                ($"{CombatantRow.Compact(credited),8}{(estimated ? " ~" : "  ")}", SourceBrush(AbilitySource.Raid)),
                ($"  {from}", Services.ClassColors.Neutral));
        }
        var unattributed = credits.Where(c => c.Granters.Count == 0).ToList();
        if (unattributed.Count > 0)
        {
            ReportLine(
                ("unattributed".PadRight(18), Services.ClassColors.Neutral),
                (string.Join(" · ", unattributed
                    .OrderByDescending(c => c.Damage)
                    .Take(4)
                    .Select(c => $"{c.Ability} {CombatantRow.Compact(c.Damage)} (no {JoinClasses(c.GrantingClasses)} detected)")),
                    Services.ClassColors.Neutral));
        }
        if (credits.Any(c => c.Estimated))
            ReportLine(("~ estimated: split evenly among same-class granters — the log names no caster", Services.ClassColors.Neutral));
    }

    private static string JoinClasses(IReadOnlyList<string> classes) =>
        classes.Count == 0 ? "granting class" : string.Join("/", classes);

    /// <summary>Combatant scope: if THIS ally granted raid buffs, show what
    /// their buffs did for everyone — the support-class credit line.</summary>
    private void RenderGrantedToRaid(CombatantRow row, string? detected)
    {
        if (detected is null)
            return;
        var fight = ResolveFight();
        var allies = FightAllyCombatants(fight);
        if (allies.Count == 0)
            return;
        var tags = SourceReportTags(fight);
        List<(string Name, SourceTally T)> rows = [];
        foreach (var (name, combatants) in allies
            .GroupBy(t => t.Name)
            .Select(g => (g.Key, g.Select(t => t.C).ToList())))
        {
            var cls = tags.TryGetValue(name.ToUpperInvariant(), out var tag) ? tag.Class.ClassName : null;
            var tally = AccumulateSources(combatants, cls);
            if (tally.Total > 0)
                rows.Add((name, tally));
        }
        var (credits, _) = ComputeRaidCredits(rows);
        var mine = credits
            .Where(c => c.Granters.Contains(row.Name, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Damage)
            .ToList();
        if (mine.Count == 0)
            return;
        var credited = mine.Sum(c => c.Damage / Math.Max(1, c.Granters.Count));
        var estimated = mine.Any(c => c.Estimated);
        ReportLine(("", Services.ClassColors.Neutral));
        ReportLine(
            ("GRANTED TO THE RAID".PadRight(28), Services.ClassColors.TreeHeader),
            ($"{CombatantRow.Compact(credited),9} credited{(estimated ? " ~" : "")}", SourceBrush(AbilitySource.Raid)));
        foreach (var credit in mine.Take(8))
        {
            ReportLine(
                ("".PadRight(9), Services.ClassColors.TreeText),
                (credit.Ability.PadRight(28), Services.ClassColors.TreeText),
                ($"{CombatantRow.Compact(credit.Damage / Math.Max(1, credit.Granters.Count)),9}"
                    + (credit.Estimated ? $"  (split {credit.Granters.Count} ways)" : ""),
                    SourceBrush(AbilitySource.Raid)));
        }
        if (estimated)
            ReportLine(("~ estimated: split evenly among same-class granters — the log names no caster", Services.ClassColors.Neutral));
    }

    /// <summary>Every classified ally of the resolved fight (merged or
    /// live). Aggregates are skipped — the granted-to-raid section only
    /// renders for a single fight.</summary>
    private List<(string Name, Combatant C)> FightAllyCombatants(object? fight)
    {
        List<(string, Combatant)> targets = [];
        switch (fight)
        {
            case CorrelatedEncounter merged:
            {
                var tags = manager.Classifier.Classify(merged.Primary);
                foreach (var (key, entry) in merged.MergedCombatants)
                {
                    if (!merged.MergedAllyKeys.Contains(key))
                        continue;
                    if (tags.TryGetValue(key, out var tag) && tag.Kind is CombatantKind.System or CombatantKind.Bystander)
                        continue;
                    targets.Add((entry.Combatant.Name, entry.Combatant));
                }
                break;
            }
            case Encounter encounter:
            {
                var tags = manager.Classifier.Classify(encounter);
                foreach (var ally in encounter.GetAllies())
                {
                    if (tags.TryGetValue(ally.Key, out var tag) && tag.Kind is CombatantKind.System or CombatantKind.Bystander)
                        continue;
                    targets.Add((ally.Name, ally));
                }
                break;
            }
        }
        return targets;
    }

    /// <summary>Distinguishable shades of one hue for the ability ring.</summary>
    private static SKColor ShadeOf(SKColor baseColor, int index)
    {
        var factor = 1.0f - 0.09f * index;
        return new SKColor(
            (byte)Math.Clamp(baseColor.Red * factor, 40, 255),
            (byte)Math.Clamp(baseColor.Green * factor, 40, 255),
            (byte)Math.Clamp(baseColor.Blue * factor, 40, 255));
    }
}
