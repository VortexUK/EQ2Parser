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

/// <summary>The report overlays: death, avoidance, special attacks,
/// and cross-fight lookup — text-line builders plus their charts.</summary>
public sealed partial class MainParseViewModel
{
    // ── Reports (death / avoidance / specials / lookup) ─────────────────────

    public BulkObservableCollection<LogRow> ReportRows { get; } = [];

    [ObservableProperty]
    private bool _reportChartVisible;

    [ObservableProperty]
    private string _reportChartTitle1 = "OUTCOME";

    [ObservableProperty]
    private string _reportChartTitle2 = "AVOIDANCE BREAKDOWN";

    [ObservableProperty]
    private bool _reportDonutsVisible = true;

    [ObservableProperty]
    private bool _reportCartesianVisible;

    [ObservableProperty]
    private ISeries[] _reportCartesianSeries = [];

    [ObservableProperty]
    private Axis[] _reportCartesianXAxes = [];

    [ObservableProperty]
    private Axis[] _reportCartesianYAxes = [];

    /// <summary>What the open report is bound to: 0 none/fight-independent,
    /// -1 a specific fight (close on switch), 1/2/3 death/avoidance/specials
    /// for one combatant (re-run for them in the new fight).</summary>
    private int _reportScope;
    private string? _reportKey;
    private string? _reportName;

    [ObservableProperty]
    private ISeries[] _reportDonutInner = [];

    [ObservableProperty]
    private ISeries[] _reportDonutOuter = [];

    private readonly System.Text.StringBuilder _reportBuilder = new();
    private readonly List<LogRow> _reportLines = [];

    private void ReportLine(params (string Text, System.Windows.Media.Brush Brush)[] parts)
    {
        List<LogSegment> segments = [.. parts.Select(t => new LogSegment(t.Text, t.Brush))];
        _reportLines.Add(new LogRow(segments, IsFocus: false));
        _reportBuilder.AppendLine(string.Concat(parts.Select(t => t.Text)));
    }

    private void OpenReport(string title, bool keepDrill = false)
    {
        ReportRows.ReplaceAll(_reportLines);
        _reportText = _reportBuilder.ToString();
        _reportLines.Clear();
        _reportBuilder.Clear();
        if (!keepDrill)
        {
            _detailKey = null;
            _detailBucket = null;
            _detailAbility = null;
        }
        DetailTitle = title;
        SwingLevel = true;
        LogLevel = false;
        ReportLevel = true;
        DrillChartVisible = false;
        DetailOpen = true;
    }

    [RelayCommand]
    private void CopyReport() => SetClipboard(_reportText);

    /// <summary>Right-click a bucket (or ability) in the drill: break it
    /// down BY THE OTHER COMBATANT — who this bucket healed / who healed
    /// them / who the damage came from or went to.</summary>
    [RelayCommand]
    private void LookupBucketByCombatant(AbilityRow? row)
    {
        if (row is null || row.IsGroupLabel || _detailKey is null)
            return;
        lock (manager.Sync)
        {
            if (ResolveFight() is not { } fight)
                return;
            var bucketName = _detailBucket ?? row.Name;
            var abilityFilter = _detailBucket is null ? null : row.Name;
            var isAutoBucket = bucketName == BucketConfig.AutoAttackOut;
            var incoming = IsIncomingBucket(bucketName);

            var instances = FightCombatantInstances(fight, _detailKey);
            if (instances.Count == 0)
                return;
            var selfName = instances[0].Name;

            var perOther = new Dictionary<string, AbilityAcc>(StringComparer.OrdinalIgnoreCase);
            foreach (var combatant in instances)
            {
                if (FindBucket(combatant, bucketName) is not { } bucket)
                    continue;
                foreach (var sw in bucket.All.Swings)
                {
                    if (sw.Ability == Combatant.KillingAbility)
                        continue;
                    if (abilityFilter is not null)
                    {
                        if (isAutoBucket)
                        {
                            if (!SwingInAutoGroup(sw, abilityFilter))
                                continue;
                        }
                        else if (sw.Ability != abilityFilter)
                        {
                            continue;
                        }
                    }
                    var other = incoming ? sw.Attacker : sw.Victim;
                    GetOrAdd(perOther, other).AddSwing(sw);
                }
            }

            var classMap = ClassMapFor(fight);
            var total = Math.Max(1, perOther.Values.Sum(a => a.Total));
            var suffix = abilityFilter is not null ? $" › {abilityFilter}" : "";
            var seconds = FightSeconds(fight);

            ReportLine(
                ("NAME".PadRight(20), ClassColors.TreeHeader),
                ("ENCDPS  SWINGS   HITS   CRIT%       AVG       MAX       TOTAL       %", ClassColors.TreeHeader));
            foreach (var (other, acc) in perOther.OrderByDescending(kv => kv.Value.Total))
            {
                ReportLine(
                    (other.PadRight(20), ClassColors.TreeText),
                    (CombatantRow.Compact(acc.Total / seconds).PadLeft(6) + "  ", ClassColors.SourceClass),
                    ($"{acc.Swings,6}  {acc.Hits,5}  ", ClassColors.Neutral),
                    ($"{(acc.Hits > 0 ? 100.0 * acc.Crits / acc.Hits : 0),5:F0}%  ", ClassColors.Neutral),
                    ($"{(acc.Hits > 0 ? CombatantRow.Compact((double)acc.Total / acc.Hits) : "—"),8}  ", ClassColors.TreeText),
                    ($"{(acc.Max > 0 ? CombatantRow.Compact(acc.Max) : "—"),8}  ", ClassColors.TreeText),
                    (CombatantRow.Compact(acc.Total).PadLeft(10), ClassColors.SourceClass),
                    ($"{100.0 * acc.Total / total,7:F1}%", ClassColors.Neutral));
            }

            OpenReport($"{selfName} › {bucketName}{suffix} › by combatant", keepDrill: true);

            // Doughnuts: share per combatant + rolled up by archetype.
            SKColor ColorOf(string name)
            {
                var media = ((System.Windows.Media.SolidColorBrush)ClassColors.For(
                    classMap.GetValueOrDefault(name))).Color;
                return new SKColor(media.R, media.G, media.B);
            }
            ReportChartTitle1 = "SHARE BY COMBATANT";
            ReportChartTitle2 = "BY ARCHETYPE";
            ReportDonutInner = [.. perOther
                .OrderByDescending(kv => kv.Value.Total)
                .Where(kv => kv.Value.Total > 0)
                .Take(12)
                .Select(ISeries (kv) => Ring(kv.Key, kv.Value.Total, ColorOf(kv.Key), total, 44))];
            var byArch = perOther
                .Where(kv => kv.Value.Total > 0)
                .GroupBy(kv => classMap.GetValueOrDefault(kv.Key) is { } cls
                    ? ClassColors.For(cls) == ClassColors.Fighter ? "Fighters"
                    : ClassColors.For(cls) == ClassColors.Priest ? "Priests"
                    : ClassColors.For(cls) == ClassColors.Scout ? "Scouts"
                    : ClassColors.For(cls) == ClassColors.Mage ? "Mages"
                    : "Other"
                    : "Other")
                .Select(g => (Label: g.Key, Total: g.Sum(kv => kv.Value.Total)))
                .OrderByDescending(t => t.Total)
                .ToList();
            ReportDonutOuter = [.. byArch.Select(ISeries (t) =>
            {
                var color = t.Label switch
                {
                    "Fighters" => ClassColors.Fighter,
                    "Priests" => ClassColors.Priest,
                    "Scouts" => ClassColors.Scout,
                    "Mages" => ClassColors.Mage,
                    _ => ClassColors.Neutral,
                };
                return Ring(t.Label, t.Total, new SKColor(color.Color.R, color.Color.G, color.Color.B), total, 44);
            })];
            ReportDonutsVisible = true;
            ReportCartesianVisible = false;
            ReportChartVisible = ReportDonutInner.Length > 0;
            _reportScope = -1; // fight-scoped: close on selection change
        }
    }

    /// <summary>Combatant name → detected class for the fight (all shapes).</summary>
    private Dictionary<string, string?> ClassMapFor(object fight)
    {
        Dictionary<string, string?> map = new(StringComparer.OrdinalIgnoreCase);
        void AddFrom(Encounter primary)
        {
            var tags = manager.Classifier.Classify(primary);
            foreach (var combatant in primary.Combatants.Values)
            {
                if (tags.TryGetValue(combatant.Key, out var tag))
                    map[combatant.Name] = tag.Class.ClassName;
            }
        }
        switch (fight)
        {
            case Encounter e: AddFrom(e); break;
            case CorrelatedEncounter m: AddFrom(m.Primary); break;
            case AggregateFights a:
                foreach (var f in a.Fights)
                    AddFrom(f.Primary);
                break;
        }
        return map;
    }

    private void RecordReportScope(object? parameter, int combatantScope)
    {
        switch (parameter)
        {
            case CombatantRow row:
                _reportScope = combatantScope;
                _reportKey = row.Key;
                _reportName = row.Name;
                break;
            case ParseNode:
                _reportScope = -1;
                break;
        }
    }

    /// <summary>Combatants of the current selection for a report target:
    /// a fight node reports every ally, a combatant row just that one.</summary>
    private List<(string Name, Combatant C)> ReportTargets(object? parameter, out string context)
    {
        context = "";
        List<(string, Combatant)> targets = [];
        switch (parameter)
        {
            case ParseNode { Fight: CorrelatedEncounter fight }:
            {
                context = fight.Title;
                var tags = manager.Classifier.Classify(fight.Primary);
                foreach (var (key, entry) in fight.MergedCombatants)
                {
                    if (!fight.MergedAllyKeys.Contains(key))
                        continue;
                    if (tags.TryGetValue(key, out var tag) && tag.Kind is CombatantKind.System or CombatantKind.Bystander)
                        continue;
                    targets.Add((entry.Combatant.Name, entry.Combatant));
                }
                break;
            }
            case CombatantRow row when ResolveFight() is { } fight:
            {
                context = row.Name;
                foreach (var combatant in FightCombatantInstances(fight, row.Key))
                    targets.Add((combatant.Name, combatant));
                break;
            }
        }
        return targets;
    }

    /// <summary>Every ally death with its killing context, plus a timeline
    /// chart of when each death happened across the fight.</summary>
    [RelayCommand]
    private void DeathReport(object? parameter)
    {
        RecordReportScope(parameter, 1);
        lock (manager.Sync)
        {
            var targets = ReportTargets(parameter, out var context);
            var fightObj = parameter is ParseNode { Fight: { } nodeFight } ? nodeFight : ResolveFight();
            var (fightStart, fightSeconds) = fightObj switch
            {
                Encounter e => (e.StartTime, Math.Max(1, e.Duration.TotalSeconds)),
                CorrelatedEncounter m => (m.StartTime, Math.Max(1, m.Duration.TotalSeconds)),
                AggregateFights a when a.Fights.Count > 0 => (a.Fights[0].StartTime, Math.Max(1, SumDuration(a.Fights).TotalSeconds)),
                _ => (DateTimeOffset.MinValue, 1.0),
            };

            // Aggregate rollups plot on a CUMULATIVE fight-time axis —
            // wall-clock offsets from the first fight's start piled every
            // later fight's deaths at the right edge of the summed axis.
            double ToAxis(DateTimeOffset t)
            {
                if (fightObj is AggregateFights agg)
                {
                    double acc = 0;
                    foreach (var f in agg.Fights)
                    {
                        var dur = Math.Max(0, f.Duration.TotalSeconds);
                        if (t < f.StartTime)
                            return acc;
                        if (t <= f.EndTime)
                            return acc + Math.Min(dur, (t - f.StartTime).TotalSeconds);
                        acc += dur;
                    }
                    return acc;
                }
                return Math.Clamp((t - fightStart).TotalSeconds, 0, fightSeconds);
            }

            // The shared all-shapes map — the local CorrelatedEncounter-only
            // version left live and aggregate deaths uncoloured.
            var classByName = fightObj is null
                ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                : ClassMapFor(fightObj);

            // Collect every death with its lead-up, ordered by time.
            List<(DateTimeOffset Time, string Victim, string Killer, List<Core.Combat.Swing> Lead)> deaths = [];
            foreach (var (targetName, combatant) in targets)
            {
                if (combatant.IncomingBuckets.GetValueOrDefault(BucketConfig.AllIncomingRef) is not { } incoming)
                    continue;
                foreach (var death in incoming.All.Swings.Where(sw => sw.Damage.IsDeath))
                {
                    var lead = incoming.All.Swings
                        .Where(sw => !sw.Damage.IsDeath && sw.Time <= death.Time && sw.Damage.Number != 0)
                        .OrderBy(sw => sw.Time).ThenBy(sw => sw.TimeSorter)
                        .TakeLast(6)
                        .ToList();
                    deaths.Add((death.Time, targetName, death.Attacker, lead));
                }
            }
            deaths.Sort((a, b) => a.Time.CompareTo(b.Time));

            if (deaths.Count == 0)
            {
                ReportLine(("No deaths.", ClassColors.OutcomeWin));
                OpenReport($"{context} › death report");
                ReportChartVisible = false;
                return;
            }

            string Rel(DateTimeOffset t) =>
                TimeSpan.FromSeconds(ToAxis(t)).ToString(@"m\:ss");

            ReportLine(
                ($"{deaths.Count} death{(deaths.Count == 1 ? "" : "s")}", ClassColors.OutcomeLoss),
                ($"   first {Rel(deaths[0].Time)}   last {Rel(deaths[^1].Time)}", ClassColors.Neutral));
            ReportLine(("", ClassColors.Neutral));
            foreach (var (time, victim, killer, lead) in deaths)
            {
                ReportLine(
                    ($"{Rel(time)}  ", ClassColors.TreeHeader),
                    (victim, ClassColors.OutcomeLoss),
                    (" — killed by ", ClassColors.TreeText),
                    (killer, ClassColors.TreeHeader));
                foreach (var sw in lead)
                {
                    var isHeal = sw.Category == SwingCategory.Healing;
                    ReportLine(
                        ($"    {Rel(sw.Time)}  ", ClassColors.Neutral),
                        (isHeal ? "heal " : "hit  ", isHeal ? ClassColors.OutcomeWin : ClassColors.OutcomeLoss),
                        (sw.Damage.Number > 0 ? CombatantRow.Compact(sw.Damage.Number) : sw.Damage.ToString(), ClassColors.SourceClass),
                        ($"  {sw.Ability}", ClassColors.SourceRaid),
                        ($"  from {sw.Attacker}", ClassColors.Neutral));
                }
            }

            OpenReport($"{context} › death report");

            // Timeline: cumulative deaths step-line + a class-coloured marker
            // per death (hover = who and when).
            List<LiveChartsCore.Defaults.ObservablePoint> stepped = [new(0, 0)];
            List<ISeries> series = [];
            var index = 0;
            foreach (var (time, victim, killer, _) in deaths)
            {
                index++;
                var sec = ToAxis(time);
                stepped.Add(new(sec, index - 1));
                stepped.Add(new(sec, index));
                var media = ((System.Windows.Media.SolidColorBrush)ClassColors.For(
                    classByName.GetValueOrDefault(victim))).Color;
                var label = $"{Rel(time)}  {victim} — {killer}";
                series.Add(new ScatterSeries<LiveChartsCore.Defaults.ObservablePoint>
                {
                    Values = new LiveChartsCore.Defaults.ObservablePoint[] { new(sec, index) },
                    Name = victim,
                    GeometrySize = 12,
                    Fill = new SolidColorPaint(new SKColor(media.R, media.G, media.B)),
                    YToolTipLabelFormatter = _ => label,
                });
            }
            stepped.Add(new(fightSeconds, deaths.Count));
            series.Insert(0, new LineSeries<LiveChartsCore.Defaults.ObservablePoint>
            {
                Values = stepped,
                Name = "deaths",
                Stroke = new SolidColorPaint(new SKColor(0xA0, 0x78, 0x40, 0xB0)) { StrokeThickness = 1.6f },
                Fill = null,
                GeometrySize = 0,
                GeometryStroke = null,
                GeometryFill = null,
                LineSmoothness = 0,
            });
            ReportCartesianSeries = [.. series];
            ReportCartesianXAxes =
            [
                new Axis
                {
                    Labeler = v => TimeSpan.FromSeconds(Math.Max(0, v)).ToString(@"m\:ss"),
                    LabelsPaint = MutedPaint(),
                    TextSize = 11,
                    SeparatorsPaint = null,
                    MinLimit = 0,
                    MaxLimit = fightSeconds,
                },
            ];
            ReportCartesianYAxes =
            [
                new Axis
                {
                    Name = "deaths",
                    NamePaint = MutedPaint(),
                    NameTextSize = 11,
                    LabelsPaint = MutedPaint(),
                    TextSize = 11,
                    Labeler = v => v.ToString("F0"),
                    MinStep = 1,
                    SeparatorsPaint = SeparatorPaint(),
                    MinLimit = 0,
                    MaxLimit = deaths.Count + 1,
                },
            ];
            ReportDonutsVisible = false;
            ReportCartesianVisible = true;
            ReportChartVisible = true;
        }
    }

    /// <summary>The shared avoidance accumulation state: enemy attacks only,
    /// ward pairing by log adjacency, stoneskin as an avoid, per-kind
    /// auto/skill splits with optional actor attribution, and the ACT-style
    /// per-type damage-avoided estimate. Was duplicated (~90 lines) between
    /// the raid and per-combatant reports and already drifting.</summary>
    private sealed class AvoidanceTally
    {
        public int Attempts;
        public int Hits;
        public int Warded;
        public long WardedTotal;
        public int StoneskinAuto;
        public int StoneskinSkill;
        public long LandedAutoTotal;
        public long LandedSkillTotal;
        public int LandedAutoCount;
        public int LandedSkillCount;

        /// <summary>kind → actor → (auto, skill). Actor is "" when the
        /// caller doesn't attribute (the raid summary).</summary>
        public readonly Dictionary<string, Dictionary<string, (int Auto, int Skill)>> Avoids = new(StringComparer.Ordinal);

        private static readonly Dictionary<string, (int Auto, int Skill)> EmptyActors = [];

        public int Stoneskin => StoneskinAuto + StoneskinSkill;
        public int Avoided => Attempts - Hits - Warded;

        /// <summary>ACT-style per-type averages (fall back to the other
        /// type's average when one never landed).</summary>
        public double AvgAuto => LandedAutoCount > 0 ? (double)LandedAutoTotal / LandedAutoCount
            : LandedSkillCount > 0 ? (double)LandedSkillTotal / LandedSkillCount : 0;

        public double AvgSkill => LandedSkillCount > 0 ? (double)LandedSkillTotal / LandedSkillCount : AvgAuto;

        public double Est(int auto, int skill) => auto * AvgAuto + skill * AvgSkill;

        public Dictionary<string, (int Auto, int Skill)> AvoidsFor(string kind) =>
            Avoids.GetValueOrDefault(kind, EmptyActors);

        public int KindCount(string kind) => AvoidsFor(kind).Values.Sum(n => n.Auto + n.Skill);

        public double EstimatedAvoided => Est(StoneskinAuto, StoneskinSkill)
            + Avoids.Values.Sum(actors => actors.Values.Sum(n => Est(n.Auto, n.Skill)));
    }

    private static void AccumulateAvoidance(
        AvoidanceTally tally, Combatant combatant, HashSet<string> enemies,
        Func<Core.Combat.Swing, string>? actorOf = null)
    {
        if (combatant.IncomingBuckets.GetValueOrDefault(BucketConfig.IncomingDamage) is not { } bucket)
            return;
        // A fully warded hit logs its absorb line(s) IMMEDIATELY BEFORE the
        // "fails to inflict any damage" line — pair by log adjacency
        // (TimeSorter, same second ±1).
        List<(int Sorter, long Second, long Amount)> absorbs = [];
        if (combatant.IncomingBuckets.GetValueOrDefault(BucketConfig.HealedInc) is { } healedInc)
        {
            foreach (var heal in healedInc.All.Swings)
            {
                if (heal.DamageType == Core.Grammar.EnglishGrammar.WardAbsorbType && heal.Damage.Number > 0)
                    absorbs.Add((heal.TimeSorter, heal.Time.ToUnixTimeSeconds(), heal.Damage.Number));
            }
        }
        absorbs.Sort((a, b) => a.Sorter.CompareTo(b.Sorter));
        var absorbUsed = new bool[absorbs.Count];

        foreach (var sw in bucket.All.Swings)
        {
            if (sw.Ability == Combatant.KillingAbility || !enemies.Contains(sw.Attacker.ToUpperInvariant()))
                continue;
            tally.Attempts++;
            var isAuto = sw.Category == SwingCategory.Melee;
            switch (sw.Damage.Number)
            {
                case > 0:
                    tally.Hits++;
                    if (isAuto)
                    {
                        tally.LandedAutoCount++;
                        tally.LandedAutoTotal += sw.Damage.Number;
                    }
                    else
                    {
                        tally.LandedSkillCount++;
                        tally.LandedSkillTotal += sw.Damage.Number;
                    }
                    break;
                case 0:
                {
                    long claimed = 0;
                    var second = sw.Time.ToUnixTimeSeconds();
                    for (var a = 0; a < absorbs.Count; a++)
                    {
                        if (absorbUsed[a])
                            continue;
                        var gap = sw.TimeSorter - absorbs[a].Sorter;
                        if (gap is <= 0 or > 6 || Math.Abs(absorbs[a].Second - second) > 1)
                            continue;
                        absorbUsed[a] = true;
                        claimed += absorbs[a].Amount;
                    }
                    if (claimed > 0)
                    {
                        tally.Warded++;
                        tally.WardedTotal += claimed;
                    }
                    else if (isAuto)
                    {
                        tally.StoneskinAuto++;
                    }
                    else
                    {
                        tally.StoneskinSkill++;
                    }
                    break;
                }
                default:
                {
                    var kind = sw.Damage.Number switch
                    {
                        Core.Combat.DamageValue.MissNumber => "Miss",
                        Core.Combat.DamageValue.ResistNumber => "Resist",
                        Core.Combat.DamageValue.ParryNumber => "Parry",
                        Core.Combat.DamageValue.RiposteNumber => "Riposte",
                        Core.Combat.DamageValue.BlockNumber => "Block",
                        _ => sw.Damage.ToString() == "Counter" ? "Counter" : "Dodge",
                    };
                    var actor = actorOf?.Invoke(sw) ?? "";
                    if (!tally.Avoids.TryGetValue(kind, out var actors))
                        tally.Avoids[kind] = actors = new(StringComparer.OrdinalIgnoreCase);
                    actors.TryGetValue(actor, out var n);
                    actors[actor] = isAuto ? (n.Auto + 1, n.Skill) : (n.Auto, n.Skill + 1);
                    break;
                }
            }
        }
    }

    /// <summary>Incoming avoidance: fight nodes get the per-ally summary
    /// table; a combatant row gets the detailed view (outcome doughnut +
    /// per-kind rows with WHO defeated each attack + avoided-damage eHPS).</summary>
    [RelayCommand]
    private void AvoidanceReport(object? parameter)
    {
        RecordReportScope(parameter, 2);
        if (parameter is CombatantRow row)
        {
            CombatantAvoidanceReport(row);
            return;
        }
        lock (manager.Sync)
        {
            var targets = ReportTargets(parameter, out var context);
            var fightObj = parameter is ParseNode { Fight: { } nodeFight } ? nodeFight : ResolveFight();
            var enemies = EnemyAttackerKeys(fightObj);

            // Same machinery as the per-combatant view, per ally: enemy
            // attacks only, ward pairing, stoneskin as an avoid, per-type
            // damage-avoided estimates.
            List<(string Name, int Attempts, int Hits, int Warded, long WardedTotal,
                int Ss, int Block, int Parry, int Riposte, int Miss, int Dodge, int Resist, int Counter,
                int Avoided, double Est)> rows = [];
            foreach (var (targetName, combatants) in targets
                .GroupBy(t => t.Name)
                .Select(g => (g.Key, g.Select(t => t.C).ToList())))
            {
                var tally = new AvoidanceTally();
                foreach (var c in combatants)
                    AccumulateAvoidance(tally, c, enemies);
                if (tally.Attempts == 0)
                    continue;
                rows.Add((targetName, tally.Attempts, tally.Hits, tally.Warded, tally.WardedTotal,
                    tally.Stoneskin, tally.KindCount("Block"), tally.KindCount("Parry"), tally.KindCount("Riposte"),
                    tally.KindCount("Miss"), tally.KindCount("Dodge"), tally.KindCount("Resist"), tally.KindCount("Counter"),
                    tally.Avoided, tally.EstimatedAvoided));
            }

            ReportLine(
                ("NAME".PadRight(18), ClassColors.TreeHeader),
                ("ATTACKS  LANDED%  WARDED    SS  BLOCK  PARRY  RIP  MISS  DDG  RES  CTR   AVOID%   EST. AVOIDED", ClassColors.TreeHeader));
            int tAtt = 0, tHit = 0, tWard = 0, tSs = 0, tAvoid = 0;
            long tWardTotal = 0;
            double tEst = 0;
            static string Cell(int n, int width) => (n > 0 ? n.ToString() : "—").PadLeft(width);
            foreach (var r in rows.OrderByDescending(r => r.Attempts))
            {
                ReportLine(
                    (r.Name.PadRight(18), ClassColors.TreeText),
                    ($"{r.Attempts,7}  ", ClassColors.TreeText),
                    ($"{100.0 * r.Hits / r.Attempts,6:F1}%  ", ClassColors.OutcomeLoss),
                    (Cell(r.Warded, 6) + "  ", ClassColors.SourceRaid),
                    (Cell(r.Ss, 4) + Cell(r.Block, 7) + Cell(r.Parry, 7) + Cell(r.Riposte, 5)
                        + Cell(r.Miss, 6) + Cell(r.Dodge, 5) + Cell(r.Resist, 5) + Cell(r.Counter, 5), ClassColors.Neutral),
                    ($"{100.0 * r.Avoided / r.Attempts,8:F1}%  ", ClassColors.OutcomeWin),
                    (CombatantRow.Compact(r.Est).PadLeft(13), ClassColors.OutcomeWin));
                tAtt += r.Attempts;
                tHit += r.Hits;
                tWard += r.Warded;
                tWardTotal += r.WardedTotal;
                tSs += r.Ss;
                tAvoid += r.Avoided;
                tEst += r.Est;
            }
            if (tAtt > 0)
            {
                ReportLine(
                    ("TOTAL".PadRight(18), ClassColors.TreeHeader),
                    ($"{tAtt,7}  ", ClassColors.TreeText),
                    ($"{100.0 * tHit / tAtt,6:F1}%  ", ClassColors.OutcomeLoss),
                    (Cell(tWard, 6) + "  ", ClassColors.SourceRaid),
                    // 44 = the data rows' kind-column width (4+7+7+5+6+5+5+5)
                    // — 46 shifted the AVOID%/EST columns two chars right.
                    (Cell(tSs, 4).PadRight(44), ClassColors.Neutral),
                    ($"{100.0 * tAvoid / tAtt,8:F1}%  ", ClassColors.OutcomeWin),
                    (CombatantRow.Compact(tEst).PadLeft(13), ClassColors.OutcomeWin));
                ReportLine(
                    ($"warded total {CombatantRow.Compact(tWardTotal)} absorbed", ClassColors.SourceRaid),
                    ($"   est. avoided {CombatantRow.Compact(tEst)} raid-wide", ClassColors.OutcomeWin));
            }

            OpenReport($"{context} › avoidance report");

            // Raid-wide doughnuts, same pair as the combatant view.
            if (tAtt > 0)
            {
                ReportChartTitle1 = "OUTCOME";
                ReportChartTitle2 = "AVOIDANCE BREAKDOWN";
                ReportDonutsVisible = true;
                ReportCartesianVisible = false;
                ReportDonutInner =
                [
                    Ring("Landed", tHit, new SKColor(0xF8, 0x71, 0x71), tAtt, 44),
                    Ring("Warded", tWard, new SKColor(0x93, 0xD9, 0xFF), tAtt, 44),
                    Ring("Avoided", tAvoid, new SKColor(0x4A, 0xDE, 0x80), tAtt, 44),
                ];
                (string Label, int Count, SKColor Color)[] agg =
                [
                    ("Stoneskin", tSs, new SKColor(0xC8, 0xA9, 0x6E)),
                    ("Block", rows.Sum(r => r.Block), new SKColor(0x4A, 0xDE, 0x80)),
                    ("Parry", rows.Sum(r => r.Parry), new SKColor(0x93, 0xB4, 0xFF)),
                    ("Riposte", rows.Sum(r => r.Riposte), new SKColor(0x22, 0xD3, 0xEE)),
                    ("Miss", rows.Sum(r => r.Miss), new SKColor(0x8B, 0x90, 0xAB)),
                    ("Dodge", rows.Sum(r => r.Dodge), new SKColor(0xFB, 0xBF, 0x24)),
                    ("Resist", rows.Sum(r => r.Resist), new SKColor(0xE8, 0xBB, 0xFF)),
                    ("Counter", rows.Sum(r => r.Counter), new SKColor(0xFF, 0x9E, 0xC7)),
                ];
                ReportDonutOuter = [.. agg
                    .Where(k => k.Count > 0)
                    .Select(ISeries (k) => Ring(k.Label, k.Count, k.Color, tAvoid, 44))];
                ReportChartVisible = true;
            }
        }
    }

    private void CombatantAvoidanceReport(CombatantRow row)
    {
        lock (manager.Sync)
        {
            var targets = ReportTargets(row, out var context);
            var seconds = FightSeconds(ResolveFight());

            (string Label, SKColor Color)[] kindPalette =
            [
                ("Block", new SKColor(0x4A, 0xDE, 0x80)),
                ("Parry", new SKColor(0x93, 0xB4, 0xFF)),
                ("Riposte", new SKColor(0x22, 0xD3, 0xEE)),
                ("Miss", new SKColor(0x8B, 0x90, 0xAB)),
                ("Dodge", new SKColor(0xFB, 0xBF, 0x24)),
                ("Resist", new SKColor(0xE8, 0xBB, 0xFF)),
                ("Counter", new SKColor(0xFF, 0x9E, 0xC7)),
            ];
            // Shared accumulation, with per-(kind, actor) attribution — the
            // auto/skill split matters because autos hit far softer than
            // skills (a blended average roughly doubled the totals vs ACT).
            var tally = new AvoidanceTally();
            var enemies = EnemyAttackerKeys(ResolveFight());
            foreach (var (targetName, combatant) in targets)
                AccumulateAvoidance(tally, combatant, enemies, sw => ActorLabel(sw.Extra, targetName));

            var attempts = tally.Attempts;
            if (attempts == 0)
            {
                ReportLine(("No incoming attacks.", ClassColors.Neutral));
                OpenReport($"{context} › avoidance report");
                return;
            }

            var hitCount = tally.Hits;
            var warded = tally.Warded;
            var wardedTotal = tally.WardedTotal;
            var avgAuto = tally.AvgAuto;
            var avgSkill = tally.AvgSkill;
            var stoneskin = tally.Stoneskin;
            var avoided = tally.Avoided;
            var avoidedEst = tally.EstimatedAvoided;

            ReportLine(
                ($"{attempts} incoming attacks", ClassColors.TreeText),
                ($"   landed {hitCount}/{attempts} ({100.0 * hitCount / attempts:F1}%)", ClassColors.OutcomeLoss),
                ($"   warded {warded}/{attempts} ({100.0 * warded / attempts:F1}%)", ClassColors.SourceRaid),
                ($"   avoided {avoided}/{attempts} ({100.0 * avoided / attempts:F1}%)", ClassColors.OutcomeWin));
            ReportLine(
                ($"avg auto {CombatantRow.Compact(avgAuto)} · avg skill {CombatantRow.Compact(avgSkill)}", ClassColors.Neutral),
                ($"   est. avoided {CombatantRow.Compact(avoidedEst)} (effective {CombatantRow.Compact(avoidedEst / seconds)} HPS)", ClassColors.OutcomeWin),
                ($"   warded {CombatantRow.Compact(wardedTotal)} absorbed", ClassColors.SourceRaid));
            ReportLine(("", ClassColors.Neutral));
            ReportLine(
                ("KIND".PadRight(11), ClassColors.TreeHeader),
                ("BY".PadRight(17), ClassColors.TreeHeader),
                ("      COUNT   % SWINGS   % AVOIDS   EST. AVOIDED", ClassColors.TreeHeader));

            void Row(string kind, string by, int count, string pctAvoids, string estimate, SKColor kindColor)
            {
                ReportLine(
                    (kind.PadRight(11), new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(kindColor.Red, kindColor.Green, kindColor.Blue))),
                    (by.PadRight(17), ClassColors.TreeText),
                    ($"{count,4} / {attempts,-4}  {100.0 * count / attempts,6:F1}%   ", ClassColors.Neutral),
                    (pctAvoids.PadLeft(8), ClassColors.Neutral),
                    (estimate.PadLeft(15), ClassColors.OutcomeWin));
            }

            Row("Landed", "—", hitCount, "—", "—", new SKColor(0xF8, 0x71, 0x71));
            Row("Warded", "—", warded, "—", CombatantRow.Compact(wardedTotal), new SKColor(0x93, 0xD9, 0xFF));
            Row("Stoneskin", "—", stoneskin,
                $"{100.0 * stoneskin / Math.Max(1, avoided):F1}%",
                CombatantRow.Compact(tally.Est(tally.StoneskinAuto, tally.StoneskinSkill)), new SKColor(0xC8, 0xA9, 0x6E));
            foreach (var (label, color) in kindPalette)
            {
                var actors = tally.AvoidsFor(label);
                if (actors.Count == 0)
                    continue;
                var first = true;
                foreach (var (actor, n) in actors.OrderByDescending(kv => kv.Value.Auto + kv.Value.Skill))
                {
                    var count = n.Auto + n.Skill;
                    Row(first ? label : "", actor, count,
                        $"{100.0 * count / Math.Max(1, avoided):F1}%",
                        CombatantRow.Compact(tally.Est(n.Auto, n.Skill)), color);
                    first = false;
                }
            }
            ReportLine(
                ("TOTAL AVOIDED".PadRight(28), ClassColors.OutcomeWin),
                ($"{avoided,4} / {attempts,-4}  {100.0 * avoided / attempts,6:F1}%   ", ClassColors.TreeText),
                ("100%".PadLeft(8), ClassColors.Neutral),
                (CombatantRow.Compact(avoidedEst).PadLeft(15), ClassColors.OutcomeWin));

            OpenReport($"{context} › avoidance report");

            // Side-by-side doughnuts: outcome (of all attacks) and the
            // avoidance breakdown (shares of the avoids).
            ReportChartTitle1 = "OUTCOME";
            ReportChartTitle2 = "AVOIDANCE BREAKDOWN";
                ReportDonutsVisible = true;
                ReportCartesianVisible = false;
            ReportDonutInner =
            [
                Ring("Landed", hitCount, new SKColor(0xF8, 0x71, 0x71), attempts, 44),
                Ring("Warded", warded, new SKColor(0x93, 0xD9, 0xFF), attempts, 44),
                Ring("Avoided", avoided, new SKColor(0x4A, 0xDE, 0x80), attempts, 44),
            ];
            List<ISeries> breakdown = [];
            if (stoneskin > 0)
                breakdown.Add(Ring("Stoneskin", stoneskin, new SKColor(0xC8, 0xA9, 0x6E), avoided, 44));
            foreach (var (label, color) in kindPalette)
            {
                var count = tally.KindCount(label);
                if (count > 0)
                    breakdown.Add(Ring(label, count, color, avoided, 44));
            }
            ReportDonutOuter = [.. breakdown];
            ReportChartVisible = true;
        }
    }

    private static PieSeries<double> Ring(string label, long count, SKColor color, long total, double innerRadius) => new()
    {
        Values = new double[] { count },
        Name = $"{100.0 * count / Math.Max(1, total):F1}%  {label}",
        Fill = new SolidColorPaint(color),
        InnerRadius = innerRadius,
        HoverPushout = 5,
        ToolTipLabelFormatter = _ => $"{count} / {total}",
    };

    /// <summary>Keys of combatants classified Enemy in the current fight —
    /// the avoidance population is enemy attacks only, matching ACT (self
    /// lifetap procs and ally utility hits would otherwise flood the
    /// stoneskin/landed counts).</summary>
    private HashSet<string> EnemyAttackerKeys(object? fight)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        void AddFrom(Encounter primary)
        {
            foreach (var (key, tag) in manager.Classifier.Classify(primary))
            {
                if (tag.Kind == CombatantKind.Enemy)
                    keys.Add(key);
            }
        }
        switch (fight)
        {
            case Encounter encounter:
                AddFrom(encounter);
                break;
            case CorrelatedEncounter merged:
                AddFrom(merged.Primary);
                break;
            case AggregateFights aggregate:
                foreach (var f in aggregate.Fights)
                    AddFrom(f.Primary);
                break;
        }
        return keys;
    }

    /// <summary>Who defeated an incoming attack: the character themselves,
    /// their weapon, or another player (a helper's weapon credits the
    /// helper). Names come pre-resolved from the processor.</summary>
    private static string ActorLabel(string? extra, string targetName)
    {
        if (extra is null || !extra.StartsWith("by=", StringComparison.Ordinal))
            return targetName;
        var actor = extra[3..];
        var idx = actor.IndexOf("'s ", StringComparison.Ordinal);
        if (idx <= 0)
            idx = actor.IndexOf("' ", StringComparison.Ordinal);
        if (idx > 0)
        {
            var owner = actor[..idx];
            return owner.Equals(targetName, StringComparison.OrdinalIgnoreCase) ? $"{targetName} (weapon)" : owner;
        }
        return actor;
    }

    /// <summary>Fight-level autoattack composition: per-ally normal/multi/
    /// double/flurry/AoE with rates + crit%, raid totals, and doughnuts for
    /// the raid kind-mix and who generates the extra attacks.</summary>
    [RelayCommand]
    private void SpecialReport(object? parameter)
    {
        RecordReportScope(parameter, 3);
        lock (manager.Sync)
        {
            var targets = ReportTargets(parameter, out var context);

            // Class colours for the per-ally doughnut — the shared all-shapes
            // map (the CorrelatedEncounter-only version left live and
            // aggregate selections uncoloured).
            var fightObj = parameter is ParseNode { Fight: { } nodeFight } ? nodeFight : ResolveFight();
            var classByName = fightObj is null
                ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                : ClassMapFor(fightObj);

            List<(string Name, int Swings, int Normal, int Multi, int Flurry, int Aoe, int Hits, int Crits)> rows = [];
            foreach (var (targetName, combatants) in targets
                .GroupBy(t => t.Name)
                .Select(g => (g.Key, g.Select(t => t.C).ToList())))
            {
                int swings = 0, normal = 0, multi = 0, flurry = 0, aoe = 0, crits = 0, hits = 0;
                foreach (var c in combatants)
                {
                    if (c.OutgoingBuckets.GetValueOrDefault(BucketConfig.AutoAttackOut) is not { } bucket)
                        continue;
                    foreach (var sw in bucket.All.Swings)
                    {
                        if (sw.Ability == Combatant.KillingAbility)
                            continue;
                        swings++;
                        if (sw.Damage.Number >= 0)
                        {
                            hits++;
                            if (sw.Critical)
                                crits++;
                        }
                        switch (sw.Special)
                        {
                            case "Multi Attack": multi++; break;
                            case "Flurry": flurry++; break;
                            case "AoE Attack": aoe++; break;
                            default: normal++; break;
                        }
                    }
                }
                if (swings > 0)
                    rows.Add((targetName, swings, normal, multi, flurry, aoe, hits, crits));
            }

            ReportLine(
                ("NAME".PadRight(18), ClassColors.TreeHeader),
                ("SWINGS  NORMAL   MULTI  FLURRY   AOE   MULTI%  FLURRY%   CRIT%", ClassColors.TreeHeader));
            static string Cell(int n, int width) => (n > 0 ? n.ToString() : "—").PadLeft(width);
            int tSw = 0, tNorm = 0, tMulti = 0, tFlurry = 0, tAoe = 0, tHits = 0, tCrits = 0;
            foreach (var r in rows.OrderByDescending(r => r.Swings))
            {
                ReportLine(
                    (r.Name.PadRight(18), ClassColors.TreeText),
                    ($"{r.Swings,6}  ", ClassColors.TreeText),
                    (Cell(r.Normal, 6) + Cell(r.Multi, 8) + Cell(r.Flurry, 8) + Cell(r.Aoe, 6), ClassColors.Neutral),
                    ($"{100.0 * r.Multi / r.Swings,8:F1}%", ClassColors.SourceRaid),
                    ($"{100.0 * r.Flurry / r.Swings,8:F1}%", ClassColors.OutcomePartial),
                    ($"{(r.Hits > 0 ? 100.0 * r.Crits / r.Hits : 0),7:F1}%", ClassColors.SourceClass));
                tSw += r.Swings;
                tNorm += r.Normal;
                tMulti += r.Multi;
                tFlurry += r.Flurry;
                tAoe += r.Aoe;
                tHits += r.Hits;
                tCrits += r.Crits;
            }
            if (tSw > 0)
            {
                ReportLine(
                    ("TOTAL".PadRight(18), ClassColors.TreeHeader),
                    ($"{tSw,6}  ", ClassColors.TreeText),
                    (Cell(tNorm, 6) + Cell(tMulti, 8) + Cell(tFlurry, 8) + Cell(tAoe, 6), ClassColors.Neutral),
                    ($"{100.0 * tMulti / tSw,8:F1}%", ClassColors.SourceRaid),
                    ($"{100.0 * tFlurry / tSw,8:F1}%", ClassColors.OutcomePartial),
                    ($"{(tHits > 0 ? 100.0 * tCrits / tHits : 0),7:F1}%", ClassColors.SourceClass));
            }

            OpenReport($"{context} › special attacks");

            if (tSw > 0)
            {
                ReportChartTitle1 = "ATTACK KINDS";
                ReportChartTitle2 = "EXTRA ATTACKS BY ALLY";
                ReportDonutsVisible = true;
                ReportCartesianVisible = false;
                ReportDonutInner =
                [
                    Ring("Normal", tNorm, new SKColor(0x8B, 0x90, 0xAB), tSw, 44),
                    Ring("Multi Attack", tMulti, new SKColor(0x93, 0xB4, 0xFF), tSw, 44),
                    Ring("Flurry", tFlurry, new SKColor(0xFB, 0xBF, 0x24), tSw, 44),
                    Ring("AoE Attack", tAoe, new SKColor(0xE8, 0xBB, 0xFF), tSw, 44),
                ];
                var totalExtra = Math.Max(1, tSw - tNorm);
                ReportDonutOuter = [.. rows
                    .Select(r => (r.Name, Extra: r.Swings - r.Normal))
                    .Where(t => t.Extra > 0)
                    .OrderByDescending(t => t.Extra)
                    .Select(ISeries (t) =>
                    {
                        var media = ((System.Windows.Media.SolidColorBrush)ClassColors.For(
                            classByName.GetValueOrDefault(t.Name))).Color;
                        return Ring(t.Name, t.Extra, new SKColor(media.R, media.G, media.B), totalExtra, 44);
                    })];
                ReportChartVisible = true;
            }
        }
    }

    /// <summary>Every fight in history featuring this combatant.</summary>
    [RelayCommand]
    private void LookupCombatant(CombatantRow? row)
    {
        if (row is null)
            return;
        _reportScope = 0; // history-wide — survives fight switches
        lock (manager.Sync)
        {
            var any = false;
            foreach (var fight in manager.Correlator.History.Reverse())
            {
                if (!fight.MergedCombatants.TryGetValue(row.Key, out var entry))
                    continue;
                var c = entry.Combatant;
                if (c.Damage <= 0 && c.Healed <= 0 && c.DamageTaken <= 0)
                    continue;
                any = true;
                var seconds = Math.Max(1, fight.Duration.TotalSeconds);
                ReportLine(
                    ($"{fight.StartTime.ToLocalTime():ddd HH:mm}  ", ClassColors.Neutral),
                    (fight.Title.PadRight(30), OutcomeBrush(fight)),
                    ($"  {CombatantRow.Compact(c.Damage / seconds).PadLeft(7)} dps", ClassColors.SourceClass),
                    ($"  {CombatantRow.Compact(c.Damage).PadLeft(7)} dmg", ClassColors.TreeText),
                    (c.Healed > 0 ? $"  {CombatantRow.Compact(c.Healed / seconds).PadLeft(7)} hps" : "", ClassColors.OutcomeWin),
                    ($"  {CombatantRow.Compact(c.DamageTaken).PadLeft(7)} taken", ClassColors.Neutral),
                    (c.Deaths > 0 ? $"  {c.Deaths} deaths" : "", ClassColors.OutcomeLoss));
            }
            if (!any)
                ReportLine(("No other fights found.", ClassColors.Neutral));
            OpenReport($"{row.Name} › all fights");
        }
    }

    [RelayCommand]
    private void OpenDetail(CombatantRow? row)
    {
        if (row is null)
            return;
        _detailKey = row.Key;
        _detailBucket = null;
        _detailAbility = null;
        DetailOpen = true;
        RefreshGrid();
    }

    /// <summary>Row click inside the drill: bucket → its abilities → swings.</summary>
    [RelayCommand]
    private void DrillInto(AbilityRow? row)
    {
        if (row is null || row.IsGroupLabel)
            return;
        if (_detailBucket is null)
            _detailBucket = row.Name;
        else
            _detailAbility ??= row.Name;
        RefreshGrid();
    }

    /// <summary>Back: pop one drill level; closes at the top.</summary>
    [RelayCommand]
    private void CloseDetail()
    {
        // Any pop must force the next snapshot — the rebuild gate would
        // otherwise skip restoring the drill title/table under a closed
        // report or log view.
        _detailSig = default;
        if (ReportLevel)
        {
            ReportLevel = false;
            DrillChartVisible = false;
            ReportChartVisible = false;
            _reportScope = 0;
            if (_detailKey is null)
            {
                DetailOpen = false;
                SwingLevel = false;
                return;
            }
            // A drill sits underneath (bucket lookup) — restore its view.
            SwingLevel = _detailAbility is not null;
            _drillChartKey = ("\0", null, null);
            RefreshGrid();
            return;
        }
        if (LogLevel)
        {
            LogLevel = false;
            _drillChartKey = ("\0", null, null); // force chart rebuild
            if (_detailKey is null)
            {
                // Standalone log view (fight context menu) — nothing beneath.
                DetailOpen = false;
                SwingLevel = false;
                return;
            }
            RefreshGrid();
            return;
        }
        if (_detailAbility is not null)
        {
            _detailAbility = null;
        }
        else if (_detailBucket is not null)
        {
            _detailBucket = null;
        }
        else
        {
            DetailOpen = false;
            _detailKey = null;
            return;
        }
        RefreshGrid();
    }

    [ObservableProperty]
    private string _breadcrumb = "No encounters yet — add a log under Sources.";

    [ObservableProperty]
    private bool _inCombat;

    [ObservableProperty]
    private bool _followLive = true;

    [ObservableProperty]
    private ParseNode? _selectedNode;

    [ObservableProperty]
    private string _sortColumn = "Damage";

    [ObservableProperty]
    private bool _sortDescending = true;

    private readonly HashSet<string> _collapsedZones = new(StringComparer.Ordinal);

    [ObservableProperty]
    private bool _bossesOnly;

    partial void OnBossesOnlyChanged(bool value) => RebuildTree();

    partial void OnSelectedNodeChanged(ParseNode? value)
    {
        if (value?.Fight is null)
            return;
        if (value.Fight is LiveFollow)
        {
            // The "⚔ Live" tree node: resume following the active fight.
            FollowLive = true;
            _pinnedFight = null;
        }
        else
        {
            _pinnedFight = value.Fight;
            FollowLive = false;
            // Zone summary: a fight-scoped drill or report makes no sense
            // zone-wide — close whatever overlays the grid.
            if (value.Fight is ZoneFights)
                CloseOverlay();
        }
        FollowSelectionInOverlay();
        RefreshGrid();
    }

    /// <summary>Arrow click on a zone header: collapse/expand only — the
    /// header body selects the zone summary instead.</summary>
    [RelayCommand]
    private void ToggleZone(ParseNode? node)
    {
        if (node?.GroupKey is not { } groupKey)
            return;
        if (!_collapsedZones.Remove(groupKey))
            _collapsedZones.Add(groupKey);
        RebuildTree();
    }

    /// <summary>Keep any open report/log view coherent with the newly
    /// selected fight: combatant reports re-run for the same character
    /// (closing if they aren't in the fight); fight-scoped reports and
    /// standalone log views close back to the summary.</summary>
    private void FollowSelectionInOverlay()
    {
        if (LogLevel && _detailKey is null)
        {
            CloseOverlay();
            return;
        }
        if (!ReportLevel)
        {
            // A plain drill follows the new fight — but when that fight
            // doesn't contain the drilled combatant at all, the panel used
            // to freeze on the previous fight's data. Close it instead.
            if (DetailOpen && _detailKey is not null)
            {
                bool inFight;
                lock (manager.Sync)
                {
                    inFight = FightContains(ResolveFight(), _detailKey);
                }
                if (!inFight)
                    CloseOverlay();
            }
            return;
        }
        if (_reportScope == -1)
        {
            CloseOverlay();
            return;
        }
        if (_reportScope <= 0)
            return; // lookup — history-wide, fight-independent
        bool present;
        lock (manager.Sync)
        {
            present = FightContains(ResolveFight(), _reportKey!);
        }
        if (!present)
        {
            CloseOverlay();
            return;
        }
        var proxy = new CombatantRow { Key = _reportKey! };
        proxy.Name = _reportName ?? _reportKey!;
        switch (_reportScope)
        {
            case 1: DeathReport(proxy); break;
            case 2: AvoidanceReport(proxy); break;
            case 3: SpecialReport(proxy); break;
        }
    }

    private static bool FightContains(object? fight, string key) => fight switch
    {
        Encounter e => e.Combatants.ContainsKey(key),
        CorrelatedEncounter m => m.MergedCombatants.ContainsKey(key),
        AggregateFights a => a.Fights.Any(f => f.MergedCombatants.ContainsKey(key)),
        _ => false,
    };

    private void CloseOverlay()
    {
        _detailSig = default;
        DetailOpen = false;
        ReportLevel = false;
        LogLevel = false;
        SwingLevel = false;
        ReportChartVisible = false;
        DrillChartVisible = false;
        _detailKey = null;
        _reportScope = 0;
    }

    [RelayCommand]
    private void SortBy(string? column)
    {
        if (column is null)
            return;
        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = column != "Name" && column != "Class";
        }
        RefreshGrid();
    }

    private DateTimeOffset _lastBusyRefresh;

    /// <summary>Shell tick (~100ms on the UI thread).</summary>
    public void Refresh()
    {
        // During a bulk import every tick would rebuild a churning tree and
        // re-classify a churning "current" fight, starving the pump of the
        // sync lock — throttle the whole page to ~0.5Hz until caught up.
        if (manager.ImportBusy)
        {
            var now = DateTimeOffset.Now;
            if (now - _lastBusyRefresh < TimeSpan.FromSeconds(2))
                return;
            _lastBusyRefresh = now;
        }
        RebuildTreeIfChanged();
        RefreshGrid();
    }

}
