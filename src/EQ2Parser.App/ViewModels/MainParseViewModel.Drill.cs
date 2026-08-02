using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace EQ2Parser.App.ViewModels;

/// <summary>The drill-down snapshot: bucket → ability → swing tables
/// derived from the resolved fight at the current drill position.</summary>
public sealed partial class MainParseViewModel
{
    // ── Drill-down snapshot ─────────────────────────────────────────────────

    private sealed record AbilityData(
        string Name, string Source, string Types, int Swings, int Hits, int Crits, long Max, long Total, double Dps,
        double? FreqSeconds = null);

    private sealed record DetailData(
        string Title, string NameHeader, bool SortTable, bool Bars, bool IsSwingLevel,
        List<AbilityData>? Table, List<SwingRow>? Swings, DrillChart? Chart = null);

    private sealed class AbilityAcc
    {
        public int Swings;
        public int Hits;
        public int Crits;
        public long Max;
        public long Total;
        public readonly SortedSet<string> Types = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(double Time, string Victim)> _uses = [];

        public void AddSwing(Core.Combat.Swing swing)
        {
            Swings++;
            if (swing.Damage.Number >= 0)
            {
                Hits++;
                if (swing.Critical)
                    Crits++;
                Max = Math.Max(Max, swing.Damage.Number);
                Total += Math.Max(0, swing.Damage.Number);
            }
            AddType(swing.DamageType);
            _uses.Add((swing.Time.ToUnixTimeMilliseconds() / 1000.0, swing.Victim));
        }

        public void AddType(string type)
        {
            if (type is not ("" or "avoided" or "death" or "none" or "heal"))
                Types.Add(type);
        }

        /// <summary>Mean seconds between USES. One use = hits on DISTINCT
        /// victims within 2s (an AoE's 24 simultaneous hits); the same
        /// victim hit again is a NEW use however fast — so a 2s item proc
        /// on one target reads ~2s, not a merged blur. Successive use-start
        /// gaps average; cross-fight gaps (>5 min, aggregate views) are
        /// excluded. Null below two uses.</summary>
        private double? Frequency()
        {
            if (_uses.Count < 2)
                return null;
            _uses.Sort((a, b) => a.Time.CompareTo(b.Time));
            List<double> gaps = [];
            var volleyStart = _uses[0].Time;
            HashSet<string> volleyVictims = new(StringComparer.OrdinalIgnoreCase) { _uses[0].Victim };
            foreach (var (time, victim) in _uses.Skip(1))
            {
                if (time - volleyStart <= 2 && volleyVictims.Add(victim))
                    continue; // same use, another target
                var gap = time - volleyStart;
                if (gap is > 0 and <= 300)
                    gaps.Add(gap);
                volleyStart = time;
                volleyVictims.Clear();
                volleyVictims.Add(victim);
            }
            return gaps.Count > 0 ? gaps.Average() : null;
        }

        public AbilityData ToData(string label, string source, double seconds) =>
            new(label, source, string.Join(", ", Types), Swings, Hits, Crits, Max, Total, Total / seconds, Frequency());
    }

    /// <summary>Special-based grouping for the Auto-Attack bucket:
    /// All / Normal / Multi Attack / Flurry / AoE Attack.</summary>
    private static readonly string[] AutoAttackGroups =
        ["All", "Normal", "Multi Attack", "Flurry", "AoE Attack"];

    private static bool SwingInAutoGroup(Core.Combat.Swing swing, string group) => group switch
    {
        "All" => true,
        "Normal" => swing.Special == "None",
        _ => swing.Special == group,
    };

    /// <summary>ACT's bucket order, outgoing then incoming.</summary>
    private static readonly string[] BucketOrder =
    [
        BucketConfig.AutoAttackOut, BucketConfig.SkillOut, BucketConfig.OutgoingDamage,
        BucketConfig.HealedOut, BucketConfig.PowerDrainOut, BucketConfig.PowerReplenishOut,
        BucketConfig.CureOut, BucketConfig.ThreatOut, BucketConfig.AllOutgoingRef,
        BucketConfig.IncomingDamage, BucketConfig.HealedInc, BucketConfig.PowerDrainInc,
        BucketConfig.PowerReplenishInc, BucketConfig.CureInc, BucketConfig.ThreatInc,
        BucketConfig.AllIncomingRef,
    ];

    private static bool IsIncomingBucket(string name) =>
        name is BucketConfig.IncomingDamage or BucketConfig.AllIncomingRef || name.EndsWith("(Inc)", StringComparison.Ordinal);

    private static Bucket? FindBucket(Combatant combatant, string name) =>
        IsIncomingBucket(name)
            ? combatant.IncomingBuckets.GetValueOrDefault(name)
            : combatant.OutgoingBuckets.GetValueOrDefault(name);

    /// <summary>Runs under the manager lock — copies primitive stats out of
    /// the live buckets for the current drill depth.</summary>
    private DetailData? SnapshotDetail(object fight, string key)
    {
        List<(Combatant C, string Src)> instances = fight switch
        {
            Encounter encounter =>
                encounter.Combatants.TryGetValue(key, out var c) ? [(c, encounter.SourceId)] : [],
            CorrelatedEncounter merged =>
                merged.MergedCombatants.TryGetValue(key, out var mc) ? [(mc.Combatant, mc.AuthoritySourceId)] : [],
            AggregateFights aggregate =>
                [.. aggregate.Fights
                    .Select(f => f.MergedCombatants.TryGetValue(key, out var mc) ? ((Combatant, string)?)(mc.Combatant, mc.AuthoritySourceId) : null)
                    .Where(t => t is not null)
                    .Select(t => t!.Value)],
            _ => [],
        };
        if (instances.Count == 0)
            return null;

        var name = instances[0].C.Name;
        var detection = manager.Classifier.Identifier.Detect(instances[0].C);
        var cls = detection.ClassName is not null ? $" · {detection.ClassName}" : "";
        var chartVersion = instances.Sum(t =>
            (long)(t.C.OutgoingBuckets.GetValueOrDefault(BucketConfig.AllOutgoingRef)?.All.Swings.Count ?? 0)
            + (t.C.IncomingBuckets.GetValueOrDefault(BucketConfig.AllIncomingRef)?.All.Swings.Count ?? 0));
        var seconds = Math.Max(1, fight switch
        {
            Encounter e => e.Duration.TotalSeconds,
            CorrelatedEncounter m => m.Duration.TotalSeconds,
            AggregateFights a => SumDuration(a.Fights).TotalSeconds,
            _ => 0,
        });
        var isAutoBucket = _detailBucket == BucketConfig.AutoAttackOut;

        // Depth 1 — the combatant's buckets, canonical ACT order, with
        // explicit OUTGOING/INCOMING dividers so the directions can never
        // read as one mixed table.
        if (_detailBucket is null)
        {
            List<AbilityData> table = [];
            string? pendingDivider = "OUTGOING";
            foreach (var bucketName in BucketOrder)
            {
                if (bucketName == BucketConfig.IncomingDamage)
                    pendingDivider = "INCOMING";
                var acc = new AbilityAcc();
                foreach (var (combatant, _) in instances)
                {
                    if (FindBucket(combatant, bucketName) is not { } bucket)
                        continue;
                    var all = bucket.All;
                    acc.Swings += all.SwingCount;
                    acc.Hits += all.Hits;
                    acc.Crits += all.CritHits;
                    acc.Max = Math.Max(acc.Max, all.MaxHit);
                    acc.Total += all.Damage;
                }
                if (acc.Swings > 0)
                {
                    if (pendingDivider is not null)
                    {
                        table.Add(new AbilityData(pendingDivider, "divider", "", -1, 0, 0, 0, 0, 0));
                        pendingDivider = null;
                    }
                    table.Add(acc.ToData(bucketName, "", seconds));
                }
            }

            DrillChart? bucketChart = null;
            if (ShouldBuildDrillChart(chartVersion))
            {
                bucketChart = HiddenDrillChart;
                if (FightWindow(fight) is { } window)
                {
                    List<(string Name, SKColor Color, double[] Rates)> chartLines = [];
                    foreach (var (lineBucket, color) in DrillLineBuckets)
                    {
                        var rates = new double[window.Slots];
                        foreach (var (combatant, _) in instances)
                        {
                            if (combatant.OutgoingBuckets.GetValueOrDefault(lineBucket) is { } b)
                                AccumulateRates(rates, b.All.Swings, window.Start, window.BucketSeconds);
                        }
                        if (rates.Any(r => r > 0))
                        {
                            for (var i = 0; i < rates.Length; i++)
                                rates[i] /= window.BucketSeconds;
                            chartLines.Add((lineBucket.Replace(" (Out)", ""), color, rates));
                        }
                    }
                    if (chartLines.Count > 0)
                        bucketChart = new DrillChart(1, chartLines, window.BucketSeconds, null, null);
                }
            }
            return new DetailData($"{name}{cls}", "BUCKET", SortTable: false, Bars: false, IsSwingLevel: false, table, null, bucketChart);
        }

        // Depth 2 — inside the Auto-Attack bucket, group by attack kind
        // (All / Normal / Multi Attack / …) rather than weapon names.
        if (_detailAbility is null && isAutoBucket)
        {
            List<AbilityData> table = [];
            foreach (var group in AutoAttackGroups)
            {
                var acc = new AbilityAcc();
                foreach (var (combatant, _) in instances)
                {
                    if (FindBucket(combatant, _detailBucket) is not { } bucket)
                        continue;
                    foreach (var swing in bucket.All.Swings)
                    {
                        if (swing.Ability == Combatant.KillingAbility)
                            continue;
                        if (SwingInAutoGroup(swing, group))
                            acc.AddSwing(swing);
                    }
                }
                if (acc.Swings > 0)
                    table.Add(acc.ToData(group, "", seconds));
            }
            DrillChart? autoChart = null;
            if (ShouldBuildDrillChart(chartVersion))
            {
                List<(string, double)> slices = [.. table
                    .Where(t => t.Name != "All" && t.Total > 0)
                    .Select(t => (t.Name, (double)t.Total))];
                autoChart = slices.Count > 0
                    ? new DrillChart(2, null, 0, slices, null)
                    : HiddenDrillChart;
            }
            return new DetailData($"{name}{cls} › {_detailBucket}", "ATTACK", SortTable: false, Bars: true, IsSwingLevel: false, table, null, autoChart);
        }

        // Depth 2 — abilities within the chosen bucket.
        if (_detailAbility is null)
        {
            var abilities = new Dictionary<string, AbilityAcc>(StringComparer.Ordinal);
            foreach (var (combatant, _) in instances)
            {
                if (FindBucket(combatant, _detailBucket) is not { } bucket)
                    continue;
                foreach (var (abilityName, stats) in bucket.Abilities)
                {
                    if (abilityName is Bucket.AllAbility or Combatant.KillingAbility)
                        continue;
                    var acc = GetOrAdd(abilities, abilityName);
                    foreach (var swing in stats.Swings)
                        acc.AddSwing(swing);
                }
            }
            var classify = !IsIncomingBucket(_detailBucket);
            List<AbilityData> table = [.. abilities.Select(kv => kv.Value.ToData(
                kv.Key,
                kv.Key == Core.Grammar.EnglishGrammar.AutoAttackAbility
                    ? "autoattack"
                    : classify
                        ? manager.Classifier.Identifier.ClassifySource(kv.Key, detection.ClassName)
                            .ToString().ToLowerInvariant()
                        : "",
                seconds))];
            // Headline row: the whole bucket at a glance (your DPS), the
            // breakdown beneath. Clicking it drills into every swing —
            // Bucket.AllAbility is literally the "All" aggregate.
            if (table.Count > 1)
            {
                table.Insert(0, new AbilityData(
                    Bucket.AllAbility, "all", "",
                    table.Sum(t => t.Swings), table.Sum(t => t.Hits), table.Sum(t => t.Crits),
                    table.Max(t => t.Max), table.Sum(t => t.Total), table.Sum(t => t.Total) / seconds));
            }
            DrillChart? abilityChart = null;
            if (ShouldBuildDrillChart(chartVersion))
            {
                var ranked = table.Where(t => t.Source != "all" && t.Total > 0).OrderByDescending(t => t.Total).ToList();
                List<(string, double)> slices = [.. ranked.Take(11).Select(t => (t.Name, (double)t.Total))];
                var rest = ranked.Skip(11).Sum(t => t.Total);
                if (rest > 0)
                    slices.Add(("(other)", rest));
                abilityChart = slices.Count > 0
                    ? new DrillChart(2, null, 0, slices, null)
                    : HiddenDrillChart;
            }
            return new DetailData($"{name}{cls} › {_detailBucket}", "ABILITY", SortTable: true, Bars: true, IsSwingLevel: false, table, null, abilityChart);
        }

        // Depth 3 — the individual swings of one ability (or one attack kind
        // inside the Auto-Attack bucket).
        var title = $"{name}{cls} › {_detailBucket} › {_detailAbility}";
        var incoming = IsIncomingBucket(_detailBucket);
        List<(Core.Combat.Swing S, string Src)> collected = [];
        foreach (var (combatant, source) in instances)
        {
            if (FindBucket(combatant, _detailBucket) is not { } bucket)
                continue;
            if (isAutoBucket)
            {
                foreach (var swing in bucket.All.Swings)
                {
                    if (swing.Ability != Combatant.KillingAbility && SwingInAutoGroup(swing, _detailAbility))
                        collected.Add((swing, source));
                }
            }
            else if (bucket.Abilities.TryGetValue(_detailAbility, out var stats))
            {
                foreach (var swing in stats.Swings)
                    collected.Add((swing, source));
            }
        }
        DrillChart? heatChart = null;
        if (ShouldBuildDrillChart(chartVersion))
        {
            heatChart = HiddenDrillChart;
            if (FightWindow(fight) is { } window)
            {
                var heat = new double[window.Slots];
                AccumulateRates(heat, collected.Select(t => t.S), window.Start, window.BucketSeconds);
                heatChart = new DrillChart(3, null, window.BucketSeconds, null, heat);
            }
        }

        var signature = (key, _detailBucket, _detailAbility, collected.Count);
        if (signature == _swingSignature && SwingRows.Count > 0)
            return new DetailData(title, "ABILITY", SortTable: false, Bars: true, IsSwingLevel: true, null, null, heatChart);
        _swingSignature = signature;

        collected.Sort((a, b) =>
        {
            var byTime = a.S.Time.CompareTo(b.S.Time);
            return byTime != 0 ? byTime : a.S.TimeSorter.CompareTo(b.S.TimeSorter);
        });
        List<SwingRow> swings = [.. collected.Select(t => new SwingRow(
            t.S.Time.ToLocalTime().ToString("HH:mm:ss"),
            t.S.Damage.ToString(),
            t.S.Critical ? "crit" : "",
            t.S.Special == "None" ? "" : t.S.Special,
            t.S.DamageType,
            incoming ? t.S.Attacker : t.S.Victim,
            t.S.Time.ToUnixTimeSeconds(),
            t.Src,
            t.S.Damage.Number,
            t.S.Ability))];
        return new DetailData(title, "ABILITY", SortTable: false, Bars: true, IsSwingLevel: true, null, swings, heatChart);
    }

    private static AbilityAcc GetOrAdd(Dictionary<string, AbilityAcc> accs, string key)
    {
        if (!accs.TryGetValue(key, out var acc))
            accs[key] = acc = new AbilityAcc();
        return acc;
    }

    private static void ApplyAbilityRows(ObservableCollection<AbilityRow> rows, List<AbilityData> snapshot, bool sort, bool bars)
    {
        if (sort)
        {
            // The synthetic All headline stays pinned above the sort.
            snapshot.Sort((a, b) => (a.Source == "all", b.Source == "all") switch
            {
                (true, false) => -1,
                (false, true) => 1,
                _ => b.Total.CompareTo(a.Total),
            });
        }
        // Percent/top exclude the All headline or every share would halve.
        var real = snapshot.Where(r => r.Swings >= 0 && r.Source != "all").ToList();
        var top = real.Count > 0 ? Math.Max(1, real.Max(r => r.Total)) : 1;
        var total = Math.Max(1, real.Sum(r => r.Total));
        for (var i = 0; i < snapshot.Count; i++)
        {
            var data = snapshot[i];
            AbilityRow row;
            if (i < rows.Count)
            {
                row = rows[i];
            }
            else
            {
                row = new AbilityRow { Key = data.Name };
                rows.Add(row);
            }
            row.Key = data.Name;
            row.Name = data.Name;
            if (data.Swings < 0)
            {
                // OUTGOING/INCOMING divider.
                row.IsGroupLabel = true;
                row.Source = "";
                row.Casts = "";
                row.Hits = "";
                row.CritPct = "";
                row.Avg = "";
                row.Max = "";
                row.Total = "";
                row.Percent = "";
                row.Types = "";
                row.Dps = "";
                row.Freq = "";
                row.BarFraction = 0;
                continue;
            }
            row.IsGroupLabel = false;
            row.Types = data.Types;
            row.Dps = data.Total > 0 ? CombatantRow.Compact(data.Dps) : "";
            row.Source = data.Source is "system" or "all" ? "" : data.Source;
            row.SourceBrush = data.Source switch
            {
                "class" => ClassColors.SourceClass,
                "raid" => ClassColors.SourceRaid,
                "item" => ClassColors.SourceItem,
                _ => ClassColors.Neutral,
            };
            row.Casts = data.Swings.ToString("N0");
            row.Hits = data.Hits.ToString("N0");
            row.CritPct = data.Hits > 0 ? $"{100.0 * data.Crits / data.Hits:F0}%" : "";
            row.Freq = data.FreqSeconds is { } f
                ? f >= 60 ? $"{(int)(f / 60)}:{(int)f % 60:00}" : $"{f:0.#}s"
                : "";
            row.Avg = data.Hits > 0 ? CombatantRow.Compact((double)data.Total / data.Hits) : "";
            row.Max = data.Max > 0 ? CombatantRow.Compact(data.Max) : "";
            row.Total = CombatantRow.Compact(data.Total);
            row.Percent = $"{100.0 * data.Total / total:F0}%";
            // The All headline gets a full-width bar; abilities scale to
            // the biggest ability as before.
            row.BarFraction = bars ? (data.Source == "all" ? 1 : (double)data.Total / top) : 0;
        }
        while (rows.Count > snapshot.Count)
            rows.RemoveAt(rows.Count - 1);
    }

}
