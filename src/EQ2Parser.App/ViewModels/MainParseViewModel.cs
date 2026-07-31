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

/// <summary>One entry in the left-hand parse tree: a zone header or a fight.</summary>
public sealed class ParseNode
{
    public bool IsHeader { get; init; }
    public string Title { get; init; } = "";
    /// <summary>Encounter (live), CorrelatedEncounter (history), or
    /// AggregateFights ("All" / "All Bosses" zone rollups).</summary>
    public object? Fight { get; init; }
    /// <summary>Zone-group identity for collapse toggling (headers only).</summary>
    public string? GroupKey { get; init; }
    /// <summary>Win green / Partial amber / Loss red; gold for headers.</summary>
    public System.Windows.Media.Brush TitleBrush { get; init; } = ClassColors.TreeText;

    // Context-menu discriminators: each node kind only offers what applies.
    public bool IsFight { get; init; }
    public bool IsDeletable { get; init; }
    /// <summary>Every fight of a zone group (headers only, for zone delete/copy).</summary>
    public IReadOnlyList<CorrelatedEncounter>? GroupFights { get; init; }
}

/// <summary>A zone rollup selection: combined stats over several fights.</summary>
public sealed record AggregateFights(string Zone, string Label, IReadOnlyList<CorrelatedEncounter> Fights);

/// <summary>Tree-node sentinel: "follow the live fight" selection.</summary>
public sealed class LiveFollow
{
    public static readonly LiveFollow Instance = new();
    private LiveFollow() { }
}

/// <summary>One row of a drill-down table (ability or attacker breakdown).</summary>
public sealed partial class AbilityRow : ObservableObject
{
    public required string Key { get; set; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _source = "";

    [ObservableProperty]
    private System.Windows.Media.Brush _sourceBrush = ClassColors.Neutral;

    [ObservableProperty]
    private string _casts = "";

    [ObservableProperty]
    private string _hits = "";

    [ObservableProperty]
    private string _critPct = "";

    [ObservableProperty]
    private string _avg = "";

    [ObservableProperty]
    private string _max = "";

    [ObservableProperty]
    private string _total = "";

    [ObservableProperty]
    private string _percent = "";

    [ObservableProperty]
    private string _types = "";

    [ObservableProperty]
    private string _dps = "";

    [ObservableProperty]
    private double _barFraction;

    /// <summary>True for the OUTGOING/INCOMING divider rows in the bucket
    /// list — styled as labels, not drillable.</summary>
    [ObservableProperty]
    private bool _isGroupLabel;
}

/// <summary>One swing of the deepest drill level. Epoch/SourcePath/Amount/
/// Ability identify the swing so clicking it can open the raw log view.</summary>
public sealed record SwingRow(
    string Time, string Result, string Crit, string Special, string Type, string Other,
    long Epoch, string SourcePath, long Amount, string Ability);

/// <summary>One rendered raw-log line (colour segments + focus flag).</summary>
public sealed record LogRow(IReadOnlyList<LogSegment> Segments, bool IsFocus);

/// <summary>
/// The ACT-style Main page: zone/fight tree on the left, sortable combatant
/// grid on the right with allies and enemies in separate sections. "Follow
/// live" keeps the grid on the active fight; clicking a tree node pins it.
/// </summary>
public sealed partial class MainParseViewModel(SourceManager manager) : ObservableObject
{
    private object? _pinnedFight;
    private (int HistoryCount, bool AnyActive) _treeSignature = (-1, false);

    public ObservableCollection<ParseNode> TreeNodes { get; } = [];
    public ObservableCollection<CombatantRow> AllyRows { get; } = [];
    public ObservableCollection<CombatantRow> PetRows { get; } = [];
    public ObservableCollection<CombatantRow> EnemyRows { get; } = [];

    [ObservableProperty]
    private string _petHeader = "Pets (0)";

    [ObservableProperty]
    private string _enemyHeader = "Enemies (0)";

    // ── Chart (encounter-summary bars + average line) ───────────────────────

    [ObservableProperty]
    private bool _chartVisible;

    [ObservableProperty]
    private ISeries[] _chartSeries = [];

    [ObservableProperty]
    private Axis[] _chartXAxes = [];

    [ObservableProperty]
    private Axis[] _chartYAxes = [];

    private (object? Fight, string Metric) _chartKey;
    private long _chartVersion;
    private long _lastChartBuildMs;

    // ── Drill charts (per depth: bucket lines / ability doughnut / heat) ────

    [ObservableProperty]
    private bool _drillChartVisible;

    [ObservableProperty]
    private bool _drillCartesianVisible;

    [ObservableProperty]
    private bool _drillDonutVisible;

    [ObservableProperty]
    private ISeries[] _drillCartesianSeries = [];

    [ObservableProperty]
    private Axis[] _drillXAxes = [];

    [ObservableProperty]
    private Axis[] _drillYAxes = [];

    [ObservableProperty]
    private ISeries[] _drillDonutSeries = [];

    public SolidColorPaint DrillLegendPaint { get; } = new(new SKColor(0xB0, 0xB4, 0xC8));
    public SolidColorPaint DonutLegendPaint { get; } = new(new SKColor(0xB0, 0xB4, 0xC8));
    public SolidColorPaint ReportLegendPaint { get; } = new(new SKColor(0xB0, 0xB4, 0xC8));
    public SolidColorPaint BreakdownLegendPaint { get; } = new(new SKColor(0xB0, 0xB4, 0xC8));

    private (string?, string?, string?) _drillChartKey = ("\0", null, null);
    private long _drillChartVersion;
    private long _drillChartMs;

    // ── Drill-down state (combatant → bucket → ability → swings) ───────────

    [ObservableProperty]
    private bool _detailOpen;

    [ObservableProperty]
    private string _detailTitle = "";

    [ObservableProperty]
    private bool _swingLevel;

    [ObservableProperty]
    private string _drillNameHeader = "BUCKET";

    private string? _detailKey;
    private string? _detailBucket;
    private string? _detailAbility;
    private (string, string, string, int) _swingSignature;

    [ObservableProperty]
    private bool _logLevel;

    [ObservableProperty]
    private bool _reportLevel;

    private string _reportText = "";

    /// <summary>The swing table shows at swing depth unless a log/report view is open.</summary>
    public bool SwingTableVisible => SwingLevel && !LogLevel && !ReportLevel;

    partial void OnSwingLevelChanged(bool value) => OnPropertyChanged(nameof(SwingTableVisible));

    partial void OnLogLevelChanged(bool value) => OnPropertyChanged(nameof(SwingTableVisible));

    partial void OnReportLevelChanged(bool value) => OnPropertyChanged(nameof(SwingTableVisible));

    public ObservableCollection<AbilityRow> DrillRows { get; } = [];
    public ObservableCollection<SwingRow> SwingRows { get; } = [];
    public ObservableCollection<LogRow> LogRows { get; } = [];

    /// <summary>Deepest drill: click a swing to see the raw log around it,
    /// with the matching line highlighted and tokens colourised.</summary>
    [RelayCommand]
    private void OpenSwingLog(SwingRow? row)
    {
        if (row is null || string.IsNullOrEmpty(row.SourcePath))
            return;
        LogRows.Clear();
        var focusFound = false;
        foreach (var raw in LogWindowReader.Read(row.SourcePath, row.Epoch, beforeSeconds: 5, afterSeconds: 5))
        {
            var isFocus = false;
            if (!focusFound && LineEpoch(raw) == row.Epoch
                && (row.Amount <= 0 || raw.Contains(row.Amount.ToString("N0"), StringComparison.Ordinal))
                && (row.Ability == Core.Grammar.EnglishGrammar.AutoAttackAbility
                    || raw.Contains(row.Ability, StringComparison.Ordinal)))
            {
                isFocus = true;
                focusFound = true;
            }
            LogRows.Add(new LogRow(LogLineHighlighter.Build(raw), isFocus));
        }
        LogLevel = true;
        DrillChartVisible = false;
    }

    private static long LineEpoch(string raw)
    {
        var close = raw.IndexOf(')');
        return close > 1 && long.TryParse(raw.AsSpan(1, close - 1), out var epoch) ? epoch : -1;
    }

    // ── Context-menu commands ───────────────────────────────────────────────

    private static void SetClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception)
        {
            // Clipboard briefly owned by another process — non-fatal.
        }
    }

    /// <summary>Chat-friendly summary of a fight / rollup / zone / live combat.</summary>
    [RelayCommand]
    private void CopyNode(ParseNode? node)
    {
        if (node is null)
            return;
        string? text;
        lock (manager.Sync)
        {
            text = node switch
            {
                { GroupFights: { Count: > 0 } group } => AggregateSummary(node.Title.TrimStart('▸', '▾', ' '), group),
                { Fight: CorrelatedEncounter fight } => FightSummary(fight),
                { Fight: AggregateFights aggregate } => AggregateSummary($"{aggregate.Zone} — {aggregate.Label}", aggregate.Fights),
                { Fight: LiveFollow } => LiveSummary(),
                _ => null,
            };
        }
        SetClipboard(text);
    }

    private string FightSummary(CorrelatedEncounter fight)
    {
        var tags = manager.Classifier.Classify(fight.Primary);
        var seconds = Math.Max(1, fight.Duration.TotalSeconds);
        var parts = fight.MergedCombatants
            .Where(kv => fight.MergedAllyKeys.Contains(kv.Key)
                && tags.TryGetValue(kv.Key, out var tag) && tag.Kind == CombatantKind.Player
                && kv.Value.Combatant.Damage > 0)
            .Select(kv => (kv.Value.Combatant.Name, Dps: kv.Value.Combatant.Damage / seconds))
            .OrderByDescending(t => t.Dps)
            .Select(t => $"{t.Name} {CombatantRow.Compact(t.Dps)}");
        return $"{fight.Title} ({FmtSpan(fight.Duration)}, raid {CombatantRow.Compact(fight.EncDps)} dps): {string.Join(", ", parts)}";
    }

    private string AggregateSummary(string label, IReadOnlyList<CorrelatedEncounter> fights)
    {
        var lines = fights.Select(FightSummary);
        return $"{label} — {fights.Count} fights, {FmtSpan(SumDuration(fights))}\n{string.Join("\n", lines)}";
    }

    private string? LiveSummary()
    {
        foreach (var source in manager.Sources)
        {
            if (source.Engine.ActiveEncounter is not { } encounter)
                continue;
            var parts = encounter.GetAllies()
                .Where(a => a.Damage > 0)
                .OrderByDescending(a => a.Damage)
                .Select(a => $"{a.Name} {CombatantRow.Compact(encounter.EncDpsOf(a))}");
            return $"{encounter.Title} ({FmtSpan(encounter.Duration)}, raid {CombatantRow.Compact(encounter.EncDps)} dps): {string.Join(", ", parts)}";
        }
        return null;
    }

    /// <summary>Delete a fight (or a whole zone group) from history.</summary>
    [RelayCommand]
    private void DeleteNode(ParseNode? node)
    {
        if (node is null)
            return;
        lock (manager.Sync)
        {
            if (node.GroupFights is { } group)
            {
                foreach (var fight in group)
                {
                    manager.Correlator.Remove(fight);
                    if (ReferenceEquals(_pinnedFight, fight))
                        _pinnedFight = null;
                }
            }
            else if (node.Fight is CorrelatedEncounter fight)
            {
                manager.Correlator.Remove(fight);
                if (ReferenceEquals(_pinnedFight, fight))
                    _pinnedFight = null;
            }
        }
        if (_pinnedFight is null)
            FollowLive = true;
        _treeSignature = (-1, false);
        Refresh();
    }

    /// <summary>Fight context menu: open the raw log at the fight's start.</summary>
    [RelayCommand]
    private void ViewFightLog(ParseNode? node)
    {
        if (node?.Fight is not CorrelatedEncounter fight)
            return;
        LogRows.Clear();
        foreach (var raw in LogWindowReader.Read(
            fight.Primary.SourceId, fight.StartTime.ToUnixTimeSeconds(), beforeSeconds: 2, afterSeconds: 30))
        {
            LogRows.Add(new LogRow(LogLineHighlighter.Build(raw), IsFocus: false));
        }
        _detailKey = null;
        _detailBucket = null;
        _detailAbility = null;
        DetailTitle = $"{fight.Title} › log";
        SwingLevel = true;
        LogLevel = true;
        DrillChartVisible = false;
        DetailOpen = true;
    }

    [RelayCommand]
    private void CopyCombatant(CombatantRow? row)
    {
        if (row is null)
            return;
        var cls = string.IsNullOrEmpty(row.ClassName) ? "" : $" ({row.ClassName})";
        List<string> parts = [$"{row.Damage} dmg", $"{row.Dps} dps"];
        if (row.Hps.Length > 0)
            parts.Add($"{row.Hps} hps");
        if (row.Taken.Length > 0)
            parts.Add($"{row.Taken} taken");
        if (row.Deaths.Length > 0)
            parts.Add($"{row.Deaths} deaths");
        SetClipboard($"{row.Name}{cls}: {string.Join(", ", parts)}");
    }

    /// <summary>Per-ability damage breakdown of one combatant as text.</summary>
    [RelayCommand]
    private void CopyBreakdown(CombatantRow? row)
    {
        if (row is null)
            return;
        string? text = null;
        lock (manager.Sync)
        {
            if (ResolveFight() is not { } fight)
                return;
            List<Combatant> instances = fight switch
            {
                Encounter e => e.Combatants.TryGetValue(row.Key, out var c) ? [c] : [],
                CorrelatedEncounter m => m.MergedCombatants.TryGetValue(row.Key, out var mc) ? [mc.Combatant] : [],
                AggregateFights a =>
                    [.. a.Fights
                        .Select(f => f.MergedCombatants.TryGetValue(row.Key, out var mc) ? mc.Combatant : null)
                        .Where(c => c is not null)
                        .Select(c => c!)],
                _ => [],
            };
            if (instances.Count == 0)
                return;
            var abilities = new Dictionary<string, AbilityAcc>(StringComparer.Ordinal);
            long total = 0;
            foreach (var combatant in instances)
            {
                total += combatant.Damage;
                if (!combatant.OutgoingBuckets.TryGetValue(BucketConfig.OutgoingDamage, out var bucket))
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
            var cls = string.IsNullOrEmpty(row.ClassName) ? "" : $" · {row.ClassName}";
            var lines = abilities
                .OrderByDescending(kv => kv.Value.Total)
                .Where(kv => kv.Value.Total > 0)
                .Select(kv => $"  {kv.Key} — {CombatantRow.Compact(kv.Value.Total)} ({100.0 * kv.Value.Total / Math.Max(1, total):F0}%), {kv.Value.Hits} hits, {(kv.Value.Hits > 0 ? 100.0 * kv.Value.Crits / kv.Value.Hits : 0):F0}% crit, max {CombatantRow.Compact(kv.Value.Max)}");
            text = $"{row.Name}{cls} — {CombatantRow.Compact(total)} dmg\n{string.Join("\n", lines)}";
        }
        SetClipboard(text);
    }

    [RelayCommand]
    private void CopyAbility(AbilityRow? row)
    {
        if (row is null || row.IsGroupLabel)
            return;
        SetClipboard($"{row.Name}: {row.Total} total ({row.Percent}), {row.Dps} encdps, {row.Casts} swings, {row.Hits} hits, {row.CritPct} crit, avg {row.Avg}, max {row.Max}{(row.Types.Length > 0 ? $" [{row.Types}]" : "")}");
    }

    [RelayCommand]
    private void CopySwing(SwingRow? row)
    {
        if (row is null)
            return;
        SetClipboard($"[{row.Time}] {row.Ability} {row.Result}{(row.Crit.Length > 0 ? " crit" : "")}{(row.Special.Length > 0 ? $" {row.Special}" : "")} {row.Type} → {row.Other}");
    }

    // ── Reports (death / avoidance / specials / lookup) ─────────────────────

    public ObservableCollection<LogRow> ReportRows { get; } = [];

    [ObservableProperty]
    private bool _reportChartVisible;

    [ObservableProperty]
    private string _reportChartTitle1 = "OUTCOME";

    [ObservableProperty]
    private string _reportChartTitle2 = "AVOIDANCE BREAKDOWN";

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

    private void OpenReport(string title)
    {
        ReportRows.Clear();
        foreach (var line in _reportLines)
            ReportRows.Add(line);
        _reportText = _reportBuilder.ToString();
        _reportLines.Clear();
        _reportBuilder.Clear();
        _detailKey = null;
        _detailBucket = null;
        _detailAbility = null;
        DetailTitle = title;
        SwingLevel = true;
        LogLevel = false;
        ReportLevel = true;
        DrillChartVisible = false;
        DetailOpen = true;
    }

    [RelayCommand]
    private void CopyReport() => SetClipboard(_reportText);

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
                switch (fight)
                {
                    case Encounter e when e.Combatants.TryGetValue(row.Key, out var c):
                        targets.Add((c.Name, c));
                        break;
                    case CorrelatedEncounter m when m.MergedCombatants.TryGetValue(row.Key, out var mc):
                        targets.Add((mc.Combatant.Name, mc.Combatant));
                        break;
                    case AggregateFights a:
                        foreach (var f in a.Fights)
                        {
                            if (f.MergedCombatants.TryGetValue(row.Key, out var mc2))
                                targets.Add((mc2.Combatant.Name, mc2.Combatant));
                        }
                        break;
                }
                break;
            }
        }
        return targets;
    }

    /// <summary>Every ally death with its killing context (last hits + heals).</summary>
    [RelayCommand]
    private void DeathReport(object? parameter)
    {
        RecordReportScope(parameter, 1);
        lock (manager.Sync)
        {
            var targets = ReportTargets(parameter, out var context);
            var any = false;
            foreach (var (targetName, combatant) in targets)
            {
                if (combatant.IncomingBuckets.GetValueOrDefault(BucketConfig.AllIncomingRef) is not { } incoming)
                    continue;
                foreach (var death in incoming.All.Swings.Where(sw => sw.Damage.IsDeath))
                {
                    any = true;
                    ReportLine(
                        ($"{death.Time.ToLocalTime():HH:mm:ss}  ", ClassColors.Neutral),
                        (targetName, ClassColors.OutcomeLoss),
                        (" — killed by ", ClassColors.TreeText),
                        (death.Attacker, ClassColors.TreeHeader));
                    var lead = incoming.All.Swings
                        .Where(sw => !sw.Damage.IsDeath && sw.Time <= death.Time && sw.Damage.Number != 0)
                        .OrderBy(sw => sw.Time).ThenBy(sw => sw.TimeSorter)
                        .TakeLast(6);
                    foreach (var sw in lead)
                    {
                        var isHeal = sw.Category == SwingCategory.Healing;
                        ReportLine(
                            ($"    {sw.Time.ToLocalTime():HH:mm:ss}  ", ClassColors.Neutral),
                            (isHeal ? "heal " : "hit  ", isHeal ? ClassColors.OutcomeWin : ClassColors.OutcomeLoss),
                            (sw.Damage.Number > 0 ? CombatantRow.Compact(sw.Damage.Number) : sw.Damage.ToString(), ClassColors.SourceClass),
                            ($"  {sw.Ability}", ClassColors.SourceRaid),
                            ($"  from {sw.Attacker}", ClassColors.Neutral));
                    }
                }
            }
            if (!any)
                ReportLine(("No deaths.", ClassColors.Neutral));
            OpenReport($"{context} › death report");
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
                int attempts = 0, hits = 0, warded = 0, ssAuto = 0, ssSkill = 0;
                int block = 0, parry = 0, riposte = 0, miss = 0, dodge = 0, resist = 0, counter = 0;
                int blockA = 0, parryA = 0, riposteA = 0, missA = 0, dodgeA = 0, resistA = 0, counterA = 0;
                long wardedTotal = 0, landedAutoTotal = 0, landedSkillTotal = 0;
                int landedAutoCount = 0, landedSkillCount = 0;

                foreach (var c in combatants)
                {
                    if (c.IncomingBuckets.GetValueOrDefault(BucketConfig.IncomingDamage) is not { } bucket)
                        continue;
                    List<(int Sorter, long Second, long Amount)> absorbs = [];
                    if (c.IncomingBuckets.GetValueOrDefault(BucketConfig.HealedInc) is { } healedInc)
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
                        attempts++;
                        var isAuto = sw.Category == SwingCategory.Melee;
                        switch (sw.Damage.Number)
                        {
                            case > 0:
                                hits++;
                                if (isAuto) { landedAutoCount++; landedAutoTotal += sw.Damage.Number; }
                                else { landedSkillCount++; landedSkillTotal += sw.Damage.Number; }
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
                                if (claimed > 0) { warded++; wardedTotal += claimed; }
                                else if (isAuto) ssAuto++;
                                else ssSkill++;
                                break;
                            }
                            case Core.Combat.DamageValue.MissNumber: miss++; if (isAuto) missA++; break;
                            case Core.Combat.DamageValue.ResistNumber: resist++; if (isAuto) resistA++; break;
                            case Core.Combat.DamageValue.ParryNumber: parry++; if (isAuto) parryA++; break;
                            case Core.Combat.DamageValue.RiposteNumber: riposte++; if (isAuto) riposteA++; break;
                            case Core.Combat.DamageValue.BlockNumber: block++; if (isAuto) blockA++; break;
                            default:
                                if (sw.Damage.ToString() == "Counter") { counter++; if (isAuto) counterA++; }
                                else { dodge++; if (isAuto) dodgeA++; }
                                break;
                        }
                    }
                }
                if (attempts == 0)
                    continue;

                var avgAuto = landedAutoCount > 0 ? (double)landedAutoTotal / landedAutoCount
                    : landedSkillCount > 0 ? (double)landedSkillTotal / landedSkillCount : 0;
                var avgSkill = landedSkillCount > 0 ? (double)landedSkillTotal / landedSkillCount : avgAuto;
                double Est(int auto, int total) => auto * avgAuto + (total - auto) * avgSkill;
                var ss = ssAuto + ssSkill;
                var avoided = attempts - hits - warded;
                var est = Est(ssAuto, ss) + Est(blockA, block) + Est(parryA, parry) + Est(riposteA, riposte)
                    + Est(missA, miss) + Est(dodgeA, dodge) + Est(resistA, resist) + Est(counterA, counter);
                rows.Add((targetName, attempts, hits, warded, wardedTotal,
                    ss, block, parry, riposte, miss, dodge, resist, counter, avoided, est));
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
                    (Cell(tSs, 4).PadRight(46), ClassColors.Neutral),
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
            var seconds = Math.Max(1, ResolveFight() switch
            {
                Encounter e => e.Duration.TotalSeconds,
                CorrelatedEncounter m => m.Duration.TotalSeconds,
                AggregateFights a => SumDuration(a.Fights).TotalSeconds,
                _ => 1,
            });

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
            // Per (kind, actor): avoided counts split by attack category so
            // estimates use the right average (autos hit far softer than
            // skills — a blended average roughly doubled the totals vs ACT).
            var avoidCounts = kindPalette.ToDictionary(
                k => k.Label,
                _ => new Dictionary<string, (int Auto, int Skill)>(StringComparer.OrdinalIgnoreCase),
                StringComparer.Ordinal);
            var attempts = 0;
            var hitCount = 0;
            var warded = 0;
            var ssAuto = 0;
            var ssSkill = 0;
            long landedAutoTotal = 0, landedSkillTotal = 0;
            int landedAutoCount = 0, landedSkillCount = 0;
            long wardedTotal = 0;
            var enemies = EnemyAttackerKeys(ResolveFight());

            foreach (var (targetName, combatant) in targets)
            {
                if (combatant.IncomingBuckets.GetValueOrDefault(BucketConfig.IncomingDamage) is not { } bucket)
                    continue;

                // A fully warded hit logs its absorb line(s) IMMEDIATELY
                // BEFORE the "fails to inflict any damage" line — pair by
                // log adjacency (TimeSorter).
                List<(int Sorter, long Second, long Amount)> absorbs = [];
                if (combatant.IncomingBuckets.GetValueOrDefault(BucketConfig.HealedInc) is { } healedInc)
                {
                    foreach (var heal in healedInc.All.Swings)
                    {
                        if (heal.DamageType != Core.Grammar.EnglishGrammar.WardAbsorbType || heal.Damage.Number <= 0)
                            continue;
                        absorbs.Add((heal.TimeSorter, heal.Time.ToUnixTimeSeconds(), heal.Damage.Number));
                    }
                }
                absorbs.Sort((a, b) => a.Sorter.CompareTo(b.Sorter));
                var absorbUsed = new bool[absorbs.Count];

                foreach (var sw in bucket.All.Swings)
                {
                    if (sw.Ability == Combatant.KillingAbility || !enemies.Contains(sw.Attacker.ToUpperInvariant()))
                        continue;
                    attempts++;
                    var isAuto = sw.Category == SwingCategory.Melee;
                    switch (sw.Damage.Number)
                    {
                        case > 0:
                            hitCount++;
                            if (isAuto)
                            {
                                landedAutoCount++;
                                landedAutoTotal += sw.Damage.Number;
                            }
                            else
                            {
                                landedSkillCount++;
                                landedSkillTotal += sw.Damage.Number;
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
                                warded++;
                                wardedTotal += claimed;
                            }
                            else if (isAuto)
                            {
                                ssAuto++;
                            }
                            else
                            {
                                ssSkill++;
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
                            var actor = ActorLabel(sw.Extra, targetName);
                            avoidCounts[kind].TryGetValue(actor, out var n);
                            avoidCounts[kind][actor] = isAuto ? (n.Auto + 1, n.Skill) : (n.Auto, n.Skill + 1);
                            break;
                        }
                    }
                }
            }

            if (attempts == 0)
            {
                ReportLine(("No incoming attacks.", ClassColors.Neutral));
                OpenReport($"{context} › avoidance report");
                return;
            }

            // ACT-style per-type averages (fall back to the other type's
            // average when one never landed).
            var avgAuto = landedAutoCount > 0 ? (double)landedAutoTotal / landedAutoCount
                : landedSkillCount > 0 ? (double)landedSkillTotal / landedSkillCount : 0;
            var avgSkill = landedSkillCount > 0 ? (double)landedSkillTotal / landedSkillCount : avgAuto;
            double Est(int auto, int skill) => auto * avgAuto + skill * avgSkill;

            var stoneskin = ssAuto + ssSkill;
            var avoided = attempts - hitCount - warded;
            var avoidedEst = Est(ssAuto, ssSkill)
                + avoidCounts.Values.Sum(actors => actors.Values.Sum(n => Est(n.Auto, n.Skill)));

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
                CombatantRow.Compact(Est(ssAuto, ssSkill)), new SKColor(0xC8, 0xA9, 0x6E));
            foreach (var (label, color) in kindPalette)
            {
                var actors = avoidCounts[label];
                if (actors.Count == 0)
                    continue;
                var first = true;
                foreach (var (actor, n) in actors.OrderByDescending(kv => kv.Value.Auto + kv.Value.Skill))
                {
                    var count = n.Auto + n.Skill;
                    Row(first ? label : "", actor, count,
                        $"{100.0 * count / Math.Max(1, avoided):F1}%",
                        CombatantRow.Compact(Est(n.Auto, n.Skill)), color);
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
                var count = avoidCounts[label].Values.Sum(n => n.Auto + n.Skill);
                if (count > 0)
                    breakdown.Add(Ring(label, count, color, avoided, 44));
            }
            ReportDonutOuter = [.. breakdown];
            ReportChartVisible = true;
        }
    }

    private static PieSeries<double> Ring(string label, int count, SKColor color, int total, double innerRadius) => new()
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

            // Class colours for the per-ally doughnut.
            Dictionary<string, string?> classByName = new(StringComparer.OrdinalIgnoreCase);
            var fightObj = parameter is ParseNode { Fight: { } nodeFight } ? nodeFight : ResolveFight();
            if (fightObj is CorrelatedEncounter cf)
            {
                var tags = manager.Classifier.Classify(cf.Primary);
                foreach (var (key, entry) in cf.MergedCombatants)
                {
                    if (tags.TryGetValue(key, out var tag))
                        classByName[entry.Combatant.Name] = tag.Class.ClassName;
                }
            }

            List<(string Name, int Swings, int Normal, int Multi, int Dbl, int Flurry, int Aoe, int Hits, int Crits)> rows = [];
            foreach (var (targetName, combatants) in targets
                .GroupBy(t => t.Name)
                .Select(g => (g.Key, g.Select(t => t.C).ToList())))
            {
                int swings = 0, normal = 0, multi = 0, dbl = 0, flurry = 0, aoe = 0, crits = 0, hits = 0;
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
                            case "Double Attack": dbl++; break;
                            case "Flurry": flurry++; break;
                            case "AoE Attack": aoe++; break;
                            default: normal++; break;
                        }
                    }
                }
                if (swings > 0)
                    rows.Add((targetName, swings, normal, multi, dbl, flurry, aoe, hits, crits));
            }

            ReportLine(
                ("NAME".PadRight(18), ClassColors.TreeHeader),
                ("SWINGS  NORMAL   MULTI    DBL  FLURRY   AOE   MULTI%  FLURRY%   CRIT%", ClassColors.TreeHeader));
            static string Cell(int n, int width) => (n > 0 ? n.ToString() : "—").PadLeft(width);
            int tSw = 0, tNorm = 0, tMulti = 0, tDbl = 0, tFlurry = 0, tAoe = 0, tHits = 0, tCrits = 0;
            foreach (var r in rows.OrderByDescending(r => r.Swings))
            {
                ReportLine(
                    (r.Name.PadRight(18), ClassColors.TreeText),
                    ($"{r.Swings,6}  ", ClassColors.TreeText),
                    (Cell(r.Normal, 6) + Cell(r.Multi, 8) + Cell(r.Dbl, 7) + Cell(r.Flurry, 8) + Cell(r.Aoe, 6), ClassColors.Neutral),
                    ($"{100.0 * r.Multi / r.Swings,8:F1}%", ClassColors.SourceRaid),
                    ($"{100.0 * r.Flurry / r.Swings,8:F1}%", ClassColors.OutcomePartial),
                    ($"{(r.Hits > 0 ? 100.0 * r.Crits / r.Hits : 0),7:F1}%", ClassColors.SourceClass));
                tSw += r.Swings;
                tNorm += r.Normal;
                tMulti += r.Multi;
                tDbl += r.Dbl;
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
                    (Cell(tNorm, 6) + Cell(tMulti, 8) + Cell(tDbl, 7) + Cell(tFlurry, 8) + Cell(tAoe, 6), ClassColors.Neutral),
                    ($"{100.0 * tMulti / tSw,8:F1}%", ClassColors.SourceRaid),
                    ($"{100.0 * tFlurry / tSw,8:F1}%", ClassColors.OutcomePartial),
                    ($"{(tHits > 0 ? 100.0 * tCrits / tHits : 0),7:F1}%", ClassColors.SourceClass));
            }

            OpenReport($"{context} › special attacks");

            if (tSw > 0)
            {
                ReportChartTitle1 = "ATTACK KINDS";
                ReportChartTitle2 = "EXTRA ATTACKS BY ALLY";
                ReportDonutInner =
                [
                    Ring("Normal", tNorm, new SKColor(0x8B, 0x90, 0xAB), tSw, 44),
                    Ring("Multi Attack", tMulti, new SKColor(0x93, 0xB4, 0xFF), tSw, 44),
                    Ring("Double Attack", tDbl, new SKColor(0x22, 0xD3, 0xEE), tSw, 44),
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
            }
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
        if (value is null)
            return;
        if (value is { IsHeader: true, GroupKey: { } groupKey })
        {
            // Header click toggles the zone's collapse state.
            if (!_collapsedZones.Remove(groupKey))
                _collapsedZones.Add(groupKey);
            RebuildTree();
            SelectedNode = null;
            return;
        }
        if (value.Fight is null)
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
        }
        FollowSelectionInOverlay();
        RefreshGrid();
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
            return;
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

    /// <summary>Shell tick (~100ms on the UI thread).</summary>
    public void Refresh()
    {
        RebuildTreeIfChanged();
        RefreshGrid();
    }

    // ── Tree ────────────────────────────────────────────────────────────────

    private void RebuildTreeIfChanged()
    {
        int historyCount;
        bool anyActive;
        lock (manager.Sync)
        {
            historyCount = manager.Correlator.History.Count;
            anyActive = manager.Sources.Any(s => s.Engine.InCombat);
        }
        if ((historyCount, anyActive) == _treeSignature)
            return;
        _treeSignature = (historyCount, anyActive);
        RebuildTree();
    }

    private static System.Windows.Media.Brush OutcomeBrush(CorrelatedEncounter fight) =>
        fight.GetSuccessLevel() switch
        {
            SuccessLevel.Win => ClassColors.OutcomeWin,
            SuccessLevel.Partial => ClassColors.OutcomePartial,
            SuccessLevel.Loss => ClassColors.OutcomeLoss,
            _ => ClassColors.TreeText,
        };

    private void RebuildTree()
    {
        List<ParseNode> nodes = [];
        lock (manager.Sync)
        {
            // Active combat: a Live node at the very top — clicking it
            // resumes following the current fight after browsing history.
            if (manager.Sources.Any(s => s.Engine.InCombat))
            {
                nodes.Add(new ParseNode
                {
                    Title = "⚔ Live combat",
                    Fight = LiveFollow.Instance,
                    TitleBrush = ClassColors.OutcomeWin,
                });
            }
            // Newest first: group consecutive same-zone fights, ACT-sidebar
            // style ("The Emerald Halls - [25] 18:57:04") with per-zone
            // "All" / "All Bosses" rollup nodes. Zone headers collapse on
            // click; the Bosses-only filter trims trash fights.
            var fights = manager.Correlator.History;
            List<(string Zone, List<CorrelatedEncounter> Items)> groups = [];
            foreach (var fight in fights)
            {
                if (groups.Count == 0 || !string.Equals(groups[^1].Zone, fight.Zone, StringComparison.OrdinalIgnoreCase))
                    groups.Add((fight.Zone, []));
                groups[^1].Items.Add(fight);
            }
            for (var g = groups.Count - 1; g >= 0; g--)
            {
                var (zone, items) = groups[g];
                var zoneName = string.IsNullOrEmpty(zone) ? "Unknown zone" : zone;
                var shown = BossesOnly ? items.Where(f => IsBossTitle(f.Title)).ToList() : items;
                if (shown.Count == 0)
                    continue;
                var groupKey = $"{zoneName}|{items[0].StartTime.Ticks}";
                var collapsed = _collapsedZones.Contains(groupKey);
                nodes.Add(new ParseNode
                {
                    IsHeader = true,
                    GroupKey = groupKey,
                    Title = $"{(collapsed ? "▸" : "▾")} {zoneName} - [{shown.Count}] {items[0].StartTime.ToLocalTime():HH:mm:ss}",
                    TitleBrush = ClassColors.TreeHeader,
                    IsDeletable = true,
                    GroupFights = [.. items],
                });
                if (collapsed)
                    continue;
                if (shown.Count > 1)
                {
                    if (!BossesOnly)
                    {
                        var all = items.ToArray();
                        nodes.Add(new ParseNode
                        {
                            Title = $"All - [{FmtSpan(SumDuration(all))}]",
                            Fight = new AggregateFights(zoneName, "All", all),
                        });
                    }
                    var bosses = items.Where(f => IsBossTitle(f.Title)).ToArray();
                    if (bosses.Length > 0)
                    {
                        nodes.Add(new ParseNode
                        {
                            Title = $"All Bosses - [{bosses.Length}] [{FmtSpan(SumDuration(bosses))}]",
                            Fight = new AggregateFights(zoneName, "All Bosses", bosses),
                        });
                    }
                }
                for (var i = shown.Count - 1; i >= 0; i--)
                {
                    var fight = shown[i];
                    var sources = fight.Sources.Count > 1 ? $" ·{fight.Sources.Count}L" : "";
                    nodes.Add(new ParseNode
                    {
                        Title = $"{fight.Title} - [{FmtSpan(fight.Duration)}] {fight.StartTime.ToLocalTime():HH:mm:ss}{sources}",
                        Fight = fight,
                        TitleBrush = OutcomeBrush(fight),
                        IsFight = true,
                        IsDeletable = true,
                    });
                }
            }
        }

        TreeNodes.Clear();
        foreach (var node in nodes)
            TreeNodes.Add(node);
    }

    /// <summary>Trash mobs are articled ("a bloom custodian"); named bosses
    /// are not. Placeholder-titled scraps are never bosses.</summary>
    private static bool IsBossTitle(string title) =>
        title != Encounter.PlaceholderTitle
        && !title.StartsWith("a ", StringComparison.Ordinal)
        && !title.StartsWith("an ", StringComparison.Ordinal);

    private static TimeSpan SumDuration(IReadOnlyList<CorrelatedEncounter> fights)
    {
        var total = TimeSpan.Zero;
        foreach (var fight in fights)
            total += fight.Duration;
        return total;
    }

    private static string FmtSpan(TimeSpan span) =>
        span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");

    // ── Grid ────────────────────────────────────────────────────────────────

    private sealed record RowData(
        string Key, string Name, string Cls, System.Windows.Media.Brush Brush, bool IsPet,
        double Seconds, long Damage, double Dps, double Hps, long Taken, int Deaths);

    private void RefreshGrid()
    {
        List<RowData> allies = [];
        List<RowData> pets = [];
        List<RowData> enemies = [];
        string breadcrumb;
        var live = false;
        DetailData? detail = null;
        ChartData? chart = null;
        object? resolvedFight;

        lock (manager.Sync)
        {
            var fight = ResolveFight();
            if (fight is null)
                return;
            resolvedFight = fight;
            if (DetailOpen && _detailKey is not null)
                detail = SnapshotDetail(fight, _detailKey);

            switch (fight)
            {
                case Encounter encounter:
                    live = encounter.Active;
                    breadcrumb = Describe(encounter.Zone, encounter.Title, encounter.Duration, encounter.EncDps, live);
                    SnapshotEncounter(encounter, allies, pets, enemies);
                    break;
                case CorrelatedEncounter merged:
                    breadcrumb = Describe(merged.Zone, merged.Title, merged.Duration, merged.EncDps, live: false);
                    SnapshotMerged(merged, allies, pets, enemies);
                    break;
                case AggregateFights aggregate:
                {
                    SnapshotAggregate(aggregate, allies, pets, enemies);
                    var seconds = Math.Max(1, SumDuration(aggregate.Fights).TotalSeconds);
                    var allyDamage = allies.Sum(r => r.Damage) + pets.Sum(r => r.Damage);
                    breadcrumb = Describe(
                        aggregate.Zone, $"{aggregate.Label} ({aggregate.Fights.Count} fights)",
                        SumDuration(aggregate.Fights), allyDamage / seconds, live: false);
                    break;
                }
                default:
                    return;
            }

            chart = MaybeSnapshotChart(resolvedFight, allies);
        }

        Breadcrumb = breadcrumb;
        InCombat = live;
        PetHeader = $"Pets ({pets.Count})";
        EnemyHeader = $"Enemies ({enemies.Count})";
        if (chart is not null)
            ApplyChart(chart);
        Apply(AllyRows, Sort(allies));
        Apply(PetRows, Sort(pets));
        Apply(EnemyRows, Sort(enemies));

        if (detail is not null)
        {
            DetailTitle = LogLevel ? $"{detail.Title} › log" : detail.Title;
            DrillNameHeader = detail.NameHeader;
            SwingLevel = detail.IsSwingLevel;
            if (detail.Table is not null)
                ApplyAbilityRows(DrillRows, detail.Table, sort: detail.SortTable, bars: detail.Bars);
            // Swings == null at swing level means "unchanged — keep rows".
            if (detail.Swings is not null)
            {
                SwingRows.Clear();
                foreach (var swing in detail.Swings)
                    SwingRows.Add(swing);
            }
            if (detail.Chart is { } drillChart && !LogLevel)
                ApplyDrillChart(drillChart);
        }
    }

    // ── Chart snapshot ──────────────────────────────────────────────────────

    private sealed record ChartData(string MetricLabel, bool IsRollup, List<(string Label, double Value, SKColor Color)> Columns);

    private static readonly SKColor ChartGold = new(0xC8, 0xA9, 0x6E, 0xB0);

    private string ChartMetric => SortColumn switch
    {
        "Hps" => "HPS",
        "Taken" => "Taken",
        _ => "DPS",
    };

    private static double MetricOf(RowData row, string metric) => metric switch
    {
        "HPS" => row.Hps,
        "Taken" => row.Taken,
        _ => row.Dps,
    };

    /// <summary>Encounter-summary chart: one column per ally (archetype
    /// colours) for single fights, one gold column per fight for rollups.
    /// Rebuilds when the fight/metric changes or (throttled ~1s) on new
    /// live data.</summary>
    private ChartData? MaybeSnapshotChart(object fight, List<RowData> allies)
    {
        var metric = ChartMetric;
        var version = allies.Sum(r => r.Damage + r.Taken) + (long)allies.Sum(r => r.Hps * 100);
        var keyChanged = !ReferenceEquals(_chartKey.Fight, fight) || _chartKey.Metric != metric;
        var now = Environment.TickCount64;
        if (!keyChanged && (version == _chartVersion || now - _lastChartBuildMs < 1000))
            return null;
        _chartKey = (fight, metric);
        _chartVersion = version;
        _lastChartBuildMs = now;

        List<(string, double, SKColor)> columns = [];
        if (fight is AggregateFights aggregate)
        {
            foreach (var f in aggregate.Fights)
            {
                var seconds = Math.Max(1, f.Duration.TotalSeconds);
                double total = 0;
                foreach (var (key, entry) in f.MergedCombatants)
                {
                    if (!f.MergedAllyKeys.Contains(key))
                        continue;
                    total += metric switch
                    {
                        "HPS" => entry.Combatant.Healed,
                        "Taken" => entry.Combatant.DamageTaken,
                        _ => entry.Combatant.Damage,
                    };
                }
                var title = f.Title.Length > 16 ? f.Title[..15] + "…" : f.Title;
                columns.Add((title, metric == "Taken" ? total : total / seconds, ChartGold));
            }
            return new ChartData(metric, IsRollup: true, columns);
        }

        foreach (var row in allies.OrderByDescending(r => MetricOf(r, metric)))
        {
            var value = MetricOf(row, metric);
            if (value <= 0)
                continue;
            var media = ((System.Windows.Media.SolidColorBrush)row.Brush).Color;
            columns.Add((row.Name, value, new SKColor(media.R, media.G, media.B, 0xC8)));
        }
        return new ChartData(metric, IsRollup: false, columns);
    }

    // LiveCharts paints carry per-canvas state — every axis/legend needs its
    // own instance, never a shared static (sharing silently drops labels).
    private static SolidColorPaint MutedPaint() => new(new SKColor(0x8B, 0x90, 0xAB));
    private static SolidColorPaint SeparatorPaint() => new(new SKColor(0x2E, 0x31, 0x50, 0x90));

    private void ApplyChart(ChartData chart)
    {
        // Per-column colours: one full-width series per distinct colour with
        // nulls elsewhere (IgnoresBarPosition keeps every bar centred), plus
        // a dashed gold average line across the set.
        var count = chart.Columns.Count;
        List<ISeries> series = [.. chart.Columns
            .Select((c, i) => (c, i))
            .GroupBy(t => t.c.Color)
            .Select(ISeries (group) =>
            {
                var values = new double?[count];
                foreach (var (c, i) in group)
                    values[i] = Math.Round(c.Value);
                return new ColumnSeries<double?>
                {
                    Values = values,
                    Name = chart.MetricLabel,
                    Fill = new SolidColorPaint(group.Key),
                    IgnoresBarPosition = true,
                    MaxBarWidth = 34,
                    Rx = 3,
                    Ry = 3,
                };
            })];
        if (count > 1)
        {
            var average = Math.Round(chart.Columns.Average(c => c.Value));
            series.Add(new LineSeries<double>
            {
                Values = [.. Enumerable.Repeat(average, count)],
                Name = "average",
                Stroke = new SolidColorPaint(new SKColor(0xE8, 0xD5, 0xA3, 0xC0))
                {
                    StrokeThickness = 1.6f,
                    PathEffect = new LiveChartsCore.SkiaSharpView.Painting.Effects.DashEffect([6f, 5f]),
                },
                Fill = null,
                GeometrySize = 0,
                GeometryStroke = null,
                GeometryFill = null,
                LineSmoothness = 0,
            });
        }
        ChartSeries = [.. series];
        ChartXAxes =
        [
            new Axis
            {
                Labels = chart.Columns.Select(c => c.Label).ToArray(),
                LabelsPaint = MutedPaint(),
                LabelsRotation = -35,
                TextSize = 10.5,
                SeparatorsPaint = null,
            },
        ];
        ChartYAxes =
        [
            new Axis
            {
                Name = chart.IsRollup ? $"raid {chart.MetricLabel}" : chart.MetricLabel,
                NamePaint = MutedPaint(),
                NameTextSize = 11,
                LabelsPaint = MutedPaint(),
                TextSize = 11,
                Labeler = v => CombatantRow.Compact(v),
                SeparatorsPaint = SeparatorPaint(),
                MinLimit = 0,
            },
        ];
        ChartVisible = ChartSeries.Length > 0;
    }

    // ── Drill chart data + rendering ────────────────────────────────────────

    /// <summary>Mode 0 = hidden, 1 = bucket activity lines over time,
    /// 2 = ability doughnut, 3 = heat strip over time.</summary>
    private sealed record DrillChart(
        int Mode,
        List<(string Name, SKColor Color, double[] Rates)>? Lines,
        double BucketSeconds,
        List<(string Label, double Value)>? Slices,
        double[]? Heat);

    private static readonly (string Bucket, SKColor Color)[] DrillLineBuckets =
    [
        (BucketConfig.AutoAttackOut, new SKColor(0xC8, 0xA9, 0x6E)),
        (BucketConfig.SkillOut, new SKColor(0x93, 0xB4, 0xFF)),
        (BucketConfig.HealedOut, new SKColor(0x4A, 0xDE, 0x80)),
        (BucketConfig.PowerDrainOut, new SKColor(0x00, 0xE5, 0xFF)),
        (BucketConfig.PowerReplenishOut, new SKColor(0x22, 0xD3, 0xEE)),
        (BucketConfig.CureOut, new SKColor(0xE8, 0xBB, 0xFF)),
        (BucketConfig.ThreatOut, new SKColor(0xF8, 0x71, 0x71)),
    ];

    private static (DateTimeOffset Start, double BucketSeconds, int Slots)? FightWindow(object fight)
    {
        DateTimeOffset start, end;
        switch (fight)
        {
            case Encounter encounter:
                start = encounter.StartTime;
                end = encounter.EndTime;
                break;
            case CorrelatedEncounter merged:
                start = merged.StartTime;
                end = merged.EndTime;
                break;
            default:
                return null;
        }
        var duration = Math.Max(1, (end - start).TotalSeconds);
        var bucketSeconds = Math.Clamp(Math.Ceiling(duration / 60), 2, 30);
        return (start, bucketSeconds, (int)(duration / bucketSeconds) + 1);
    }

    /// <summary>Throttle: rebuild on drill-path change; otherwise only when
    /// the underlying data grew (live fight), at most ~1s. Ended fights
    /// build once and stay still.</summary>
    private bool ShouldBuildDrillChart(long version)
    {
        var key = (_detailKey, _detailBucket, _detailAbility);
        var now = Environment.TickCount64;
        if (key != _drillChartKey)
        {
            _drillChartKey = key;
            _drillChartVersion = version;
            _drillChartMs = now;
            return true;
        }
        if (version == _drillChartVersion || now - _drillChartMs < 1000)
            return false;
        _drillChartVersion = version;
        _drillChartMs = now;
        return true;
    }

    private static readonly DrillChart HiddenDrillChart = new(0, null, 0, null, null);

    private static void AccumulateRates(double[] rates, IEnumerable<Core.Combat.Swing> swings, DateTimeOffset start, double bucketSeconds)
    {
        foreach (var swing in swings)
        {
            if (swing.Damage.Number <= 0)
                continue;
            var slot = (int)((swing.Time - start).TotalSeconds / bucketSeconds);
            if (slot >= 0 && slot < rates.Length)
                rates[slot] += swing.Damage.Number;
        }
    }

    private void ApplyDrillChart(DrillChart chart)
    {
        switch (chart.Mode)
        {
            case 1 when chart.Lines is { } lines:
            {
                var bucket = chart.BucketSeconds;
                DrillCartesianSeries = [.. lines.Select(ISeries (line) => new LineSeries<double>
                {
                    Values = line.Rates,
                    Name = line.Name,
                    Stroke = new SolidColorPaint(line.Color) { StrokeThickness = 2 },
                    Fill = null,
                    GeometrySize = 0,
                    GeometryStroke = null,
                    GeometryFill = null,
                    LineSmoothness = 0.4,
                })];
                DrillXAxes = [TimeAxis(bucket)];
                DrillYAxes =
                [
                    new Axis
                    {
                        LabelsPaint = MutedPaint(),
                        TextSize = 11,
                        Labeler = v => CombatantRow.Compact(v),
                        SeparatorsPaint = SeparatorPaint(),
                        MinLimit = 0,
                    },
                ];
                DrillCartesianVisible = true;
                DrillDonutVisible = false;
                break;
            }
            case 2 when chart.Slices is { } slices:
            {
                var total = Math.Max(1, slices.Sum(s => s.Value));
                DrillDonutSeries = [.. slices.Select(ISeries (slice, i) =>
                {
                    var share = slice.Value / total;
                    return new PieSeries<double>
                    {
                        Values = new[] { Math.Round(slice.Value) },
                        Name = $"{share * 100:F0}%  {slice.Label}",
                        Fill = new SolidColorPaint(SKColor.FromHsv(i * 360f / Math.Max(1, slices.Count), 44f, 82f)),
                        InnerRadius = 50,
                        HoverPushout = 7,
                        ToolTipLabelFormatter = _ => $"{CombatantRow.Compact(slice.Value)}  ·  {share:P0}",
                    };
                })];
                DrillCartesianVisible = false;
                DrillDonutVisible = true;
                break;
            }
            case 3 when chart.Heat is { } heat:
            {
                // Heat strip: time on X, intensity = the skill's output in
                // that window (stone → gold → red).
                var points = new LiveChartsCore.Defaults.WeightedPoint[heat.Length];
                for (var i = 0; i < heat.Length; i++)
                    points[i] = new(i, 0, heat[i]);
                DrillCartesianSeries =
                [
                    new HeatSeries<LiveChartsCore.Defaults.WeightedPoint>
                    {
                        Values = points,
                        Name = "output",
                        HeatMap =
                        [
                            new LiveChartsCore.Drawing.LvcColor(28, 31, 46),
                            new LiveChartsCore.Drawing.LvcColor(200, 169, 110),
                            new LiveChartsCore.Drawing.LvcColor(248, 113, 113),
                        ],
                    },
                ];
                DrillXAxes = [TimeAxis(chart.BucketSeconds)];
                DrillYAxes = [new Axis { Labels = [""], LabelsPaint = null, SeparatorsPaint = null }];
                DrillCartesianVisible = true;
                DrillDonutVisible = false;
                break;
            }
            default:
                DrillChartVisible = false;
                DrillCartesianVisible = false;
                DrillDonutVisible = false;
                return;
        }
        DrillChartVisible = true;
    }

    private static Axis TimeAxis(double bucketSeconds) => new()
    {
        Labeler = v => TimeSpan.FromSeconds(Math.Max(0, v) * bucketSeconds).ToString(@"m\:ss"),
        LabelsPaint = MutedPaint(),
        TextSize = 11,
        SeparatorsPaint = null,
    };

    // ── Drill-down snapshot ─────────────────────────────────────────────────

    private sealed record AbilityData(
        string Name, string Source, string Types, int Swings, int Hits, int Crits, long Max, long Total, double Dps);

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
        }

        public void AddType(string type)
        {
            if (type is not ("" or "avoided" or "death" or "none" or "heal"))
                Types.Add(type);
        }

        public AbilityData ToData(string label, string source, double seconds) =>
            new(label, source, string.Join(", ", Types), Swings, Hits, Crits, Max, Total, Total / seconds);
    }

    /// <summary>Special-based grouping for the Auto-Attack bucket:
    /// All / Normal / Multi Attack / Double Attack / Flurry / AoE Attack.</summary>
    private static readonly string[] AutoAttackGroups =
        ["All", "Normal", "Multi Attack", "Double Attack", "Flurry", "AoE Attack"];

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
            DrillChart? abilityChart = null;
            if (ShouldBuildDrillChart(chartVersion))
            {
                var ranked = table.Where(t => t.Total > 0).OrderByDescending(t => t.Total).ToList();
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
            snapshot.Sort((a, b) => b.Total.CompareTo(a.Total));
        var real = snapshot.Where(r => r.Swings >= 0).ToList();
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
                row.BarFraction = 0;
                continue;
            }
            row.IsGroupLabel = false;
            row.Types = data.Types;
            row.Dps = data.Total > 0 ? CombatantRow.Compact(data.Dps) : "";
            row.Source = data.Source == "system" ? "" : data.Source;
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
            row.Avg = data.Hits > 0 ? CombatantRow.Compact((double)data.Total / data.Hits) : "";
            row.Max = data.Max > 0 ? CombatantRow.Compact(data.Max) : "";
            row.Total = CombatantRow.Compact(data.Total);
            row.Percent = $"{100.0 * data.Total / total:F0}%";
            row.BarFraction = bars ? (double)data.Total / top : 0;
        }
        while (rows.Count > snapshot.Count)
            rows.RemoveAt(rows.Count - 1);
    }

    private object? ResolveFight()
    {
        if (!FollowLive && _pinnedFight is not null)
            return _pinnedFight;
        foreach (var source in manager.Sources)
        {
            if (source.Engine.ActiveEncounter is { } active)
                return active;
        }
        if (manager.Correlator.History.Count > 0)
            return manager.Correlator.History[^1];
        return null;
    }

    private static string Describe(string zone, string title, TimeSpan duration, double dps, bool live)
    {
        var shownTitle = title == Encounter.PlaceholderTitle && live ? "Combat…" : title;
        var zonePart = string.IsNullOrEmpty(zone) ? "" : $"{zone}  |  ";
        return $"{zonePart}{shownTitle}  ·  {duration.TotalSeconds:F0}s  ·  raid {CombatantRow.Compact(dps)} dps";
    }

    private void SnapshotEncounter(Encounter encounter, List<RowData> allies, List<RowData> pets, List<RowData> enemies)
    {
        var tags = manager.Classifier.Classify(encounter);
        foreach (var combatant in encounter.Combatants.Values)
        {
            if (!tags.TryGetValue(combatant.Key, out var tag))
                continue;
            if (tag.Kind is CombatantKind.System or CombatantKind.Bystander)
                continue;
            if (combatant.Damage <= 0 && combatant.Healed <= 0 && combatant.DamageTaken <= 0)
                continue;
            var row = BuildRow(
                combatant.Key, combatant.Name, tag,
                combatant.Duration.TotalSeconds, combatant.Damage,
                encounter.EncDpsOf(combatant), encounter.EncHpsOf(combatant),
                combatant.DamageTaken, combatant.Deaths);
            BucketRow(tag, row, allies, pets, enemies);
        }
    }

    private void SnapshotMerged(CorrelatedEncounter merged, List<RowData> allies, List<RowData> pets, List<RowData> enemies)
    {
        var tags = manager.Classifier.Classify(merged.Primary);
        var seconds = Math.Max(1, merged.Duration.TotalSeconds);
        foreach (var (key, entry) in merged.MergedCombatants)
        {
            var combatant = entry.Combatant;
            if (!tags.TryGetValue(key, out var tag))
                continue;
            if (tag.Kind is CombatantKind.System or CombatantKind.Bystander)
                continue;
            if (combatant.Damage <= 0 && combatant.Healed <= 0 && combatant.DamageTaken <= 0)
                continue;
            var row = BuildRow(
                key, combatant.Name, tag,
                combatant.Duration.TotalSeconds, combatant.Damage,
                combatant.Damage / seconds, combatant.Healed / seconds,
                combatant.DamageTaken, combatant.Deaths);
            BucketRow(tag, row, allies, pets, enemies);
        }
    }

    /// <summary>Combined stats over a zone rollup — sums per combatant, with
    /// EncDPS/EncHPS over the COMBINED fight duration (ACT's "All" maths).
    /// The class/kind tag is taken from the fight with the strongest class
    /// evidence for that combatant.</summary>
    private void SnapshotAggregate(AggregateFights aggregate, List<RowData> allies, List<RowData> pets, List<RowData> enemies)
    {
        var totalSeconds = Math.Max(1, SumDuration(aggregate.Fights).TotalSeconds);
        var acc = new Dictionary<string, (string Name, CombatantTag Tag, double Seconds, long Damage, long Healed, long Taken, int Deaths)>(StringComparer.Ordinal);

        foreach (var fight in aggregate.Fights)
        {
            var tags = manager.Classifier.Classify(fight.Primary);
            foreach (var (key, entry) in fight.MergedCombatants)
            {
                var combatant = entry.Combatant;
                if (!tags.TryGetValue(key, out var tag))
                    continue;
                if (tag.Kind is CombatantKind.System or CombatantKind.Bystander)
                    continue;
                if (combatant.Damage <= 0 && combatant.Healed <= 0 && combatant.DamageTaken <= 0)
                    continue;
                if (acc.TryGetValue(key, out var existing))
                {
                    var bestTag = tag.Class.MappedAbilities > existing.Tag.Class.MappedAbilities ? tag : existing.Tag;
                    acc[key] = (existing.Name, bestTag,
                        existing.Seconds + combatant.Duration.TotalSeconds,
                        existing.Damage + combatant.Damage,
                        existing.Healed + combatant.Healed,
                        existing.Taken + combatant.DamageTaken,
                        existing.Deaths + combatant.Deaths);
                }
                else
                {
                    acc[key] = (combatant.Name, tag,
                        combatant.Duration.TotalSeconds, combatant.Damage,
                        combatant.Healed, combatant.DamageTaken, combatant.Deaths);
                }
            }
        }

        foreach (var (key, entry) in acc)
        {
            var row = BuildRow(
                key, entry.Name, entry.Tag,
                entry.Seconds, entry.Damage,
                entry.Damage / totalSeconds, entry.Healed / totalSeconds,
                entry.Taken, entry.Deaths);
            BucketRow(entry.Tag, row, allies, pets, enemies);
        }
    }

    private static void BucketRow(CombatantTag tag, RowData row, List<RowData> allies, List<RowData> pets, List<RowData> enemies)
    {
        var target = tag.Kind switch
        {
            CombatantKind.Enemy => enemies,
            CombatantKind.Pet => pets,
            _ => allies,
        };
        target.Add(row);
    }

    private static RowData BuildRow(
        string key, string name, CombatantTag tag,
        double seconds, long damage, double dps, double hps, long taken, int deaths)
    {
        var isPet = tag.Kind == CombatantKind.Pet;
        var cls = isPet
            ? (tag.PetOwner is not null ? $"pet · {tag.PetOwner}" : "pet")
            : tag.Class.ClassName ?? "";
        var brush = isPet ? ClassColors.Neutral : ClassColors.For(tag.Class.ClassName);
        return new RowData(key, name, cls, brush, isPet, seconds, damage, dps, hps, taken, deaths);
    }

    private List<RowData> Sort(List<RowData> rows)
    {
        Comparison<RowData> compare = SortColumn switch
        {
            "Name" => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            "Class" => (a, b) => string.Compare(a.Cls, b.Cls, StringComparison.OrdinalIgnoreCase),
            "Duration" => (a, b) => a.Seconds.CompareTo(b.Seconds),
            "Dps" => (a, b) => a.Dps.CompareTo(b.Dps),
            "Hps" => (a, b) => a.Hps.CompareTo(b.Hps),
            "Taken" => (a, b) => a.Taken.CompareTo(b.Taken),
            "Deaths" => (a, b) => a.Deaths.CompareTo(b.Deaths),
            _ => (a, b) => a.Damage.CompareTo(b.Damage),
        };
        rows.Sort(SortDescending ? (a, b) => compare(b, a) : compare);
        return rows;
    }

    private void Apply(ObservableCollection<CombatantRow> rows, List<RowData> snapshot)
    {
        // Row bars + % follow the sorted metric (HPS/Taken when sorted so;
        // damage otherwise), so re-sorting re-shapes the meter.
        var metric = ChartMetric;
        var top = snapshot.Count > 0 ? Math.Max(1.0, snapshot.Max(r => MetricOf(r, metric))) : 1;
        var total = Math.Max(1.0, snapshot.Sum(r => MetricOf(r, metric)));
        for (var i = 0; i < snapshot.Count; i++)
        {
            var data = snapshot[i];
            CombatantRow row;
            if (i < rows.Count)
            {
                row = rows[i];
            }
            else
            {
                row = new CombatantRow { Key = data.Key };
                rows.Add(row);
            }
            row.Key = data.Key;
            row.Name = data.Name;
            row.ClassName = data.Cls;
            row.ClassBrush = data.Brush;
            row.IsPet = data.IsPet;
            row.Duration = TimeSpan.FromSeconds(data.Seconds).ToString(@"mm\:ss");
            row.Damage = CombatantRow.Compact(data.Damage);
            row.Percent = $"{100.0 * MetricOf(data, metric) / total:F0}%";
            row.Dps = CombatantRow.Compact(data.Dps);
            row.Hps = data.Hps > 0 ? CombatantRow.Compact(data.Hps) : "";
            row.Taken = data.Taken > 0 ? CombatantRow.Compact(data.Taken) : "";
            row.Deaths = data.Deaths > 0 ? data.Deaths.ToString() : "";
            row.BarFraction = MetricOf(data, metric) / top;
        }
        while (rows.Count > snapshot.Count)
            rows.RemoveAt(rows.Count - 1);
    }
}
