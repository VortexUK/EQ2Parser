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

    /// <summary>Collapse-state glyph ("▸"/"▾"), headers only — its own
    /// clickable element so the header body is free to select the zone.</summary>
    public string Arrow { get; init; } = "";

    // Context-menu discriminators: each node kind only offers what applies.
    public bool IsFight { get; init; }
    public bool IsDeletable { get; init; }
    /// <summary>Every fight of a zone group (headers only, for zone delete/copy).</summary>
    public IReadOnlyList<CorrelatedEncounter>? GroupFights { get; init; }
}

/// <summary>A zone rollup selection: combined stats over several fights.</summary>
public sealed record AggregateFights(string Zone, string Label, IReadOnlyList<CorrelatedEncounter> Fights);

/// <summary>A zone-header selection: the encounter-list summary view. The
/// group is re-resolved from history each refresh (so a live zone gains new
/// fights); Snapshot is the click-time fallback if the group re-shapes.</summary>
public sealed record ZoneFights(string Zone, string GroupKey, IReadOnlyList<CorrelatedEncounter> Snapshot);

/// <summary>One encounter row of the zone-summary table.</summary>
public sealed record ZoneFightRow(
    CorrelatedEncounter Fight, string Title, System.Windows.Media.Brush TitleBrush,
    string Start, string Duration, string Damage, string Dps,
    string Kills, string Deaths, double BarFraction);

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
    private string _freq = "";

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
public sealed partial class MainParseViewModel : ObservableObject
{
    // Plain field (named like the old primary-ctor parameter so every
    // member reads unchanged) — primary-ctor params are only in scope in
    // the declaring part, and this class is split across partial files.
    private readonly SourceManager manager;

    public MainParseViewModel(SourceManager manager)
    {
        this.manager = manager;
        Columns = ColumnSets.Encounter(manager);
        DrillColumns = ColumnSets.Drill(manager);
        SwingColumns = ColumnSets.Swing(manager);
    }

    /// <summary>Exposed for view-owned windows (the Archive).</summary>
    public SourceManager Manager => manager;

    /// <summary>ACT-style configurable columns: encounter grid + drill table.</summary>
    public ColumnSetVm Columns { get; }

    public ColumnSetVm DrillColumns { get; }

    public ColumnSetVm SwingColumns { get; }

    private object? _pinnedFight;
    private (long Version, bool AnyActive) _treeSignature = (-1, false);

    public BulkObservableCollection<ParseNode> TreeNodes { get; } = [];
    public ObservableCollection<CombatantRow> AllyRows { get; } = [];
    public ObservableCollection<CombatantRow> PetRows { get; } = [];
    public ObservableCollection<CombatantRow> EnemyRows { get; } = [];

    // ── Zone summary (encounter list for a selected zone header) ────────────

    public BulkObservableCollection<ZoneFightRow> ZoneSummaryRows { get; } = [];

    [ObservableProperty]
    private bool _zoneSummaryOpen;

    /// <summary>Rows only rebuild when the group's identity or fight count
    /// changes — walking every fight's combatants at 10 Hz would not fly.</summary>
    private (string GroupKey, int Count, long Version) _zoneSummarySig;

    /// <summary>Drill-table rebuild gate: fight identity + drill position +
    /// view level + a cheap data version. Without it the bucket/ability
    /// tables re-derived from raw swings every 100ms tick, even for ended
    /// fights whose data can never change again.</summary>
    private (object? Fight, string? Key, string? Bucket, string? Ability, bool Swing, bool Log, long Version) _detailSig;

    private static long DetailVersion(object fight) => fight switch
    {
        // Live fights: total observed swings (O(combatants) now).
        Encounter e => e.Combatants.Values.Sum(c => (long)c.ObservedSwingCount),
        // Correlated sources are completed and immutable — only a merge
        // (new source) changes the data.
        CorrelatedEncounter m => m.Sources.Count,
        _ => 0,
    };

    /// <summary>Summary row click: jump to that fight's combatant grid.</summary>
    [RelayCommand]
    private void OpenZoneFight(ZoneFightRow? row)
    {
        if (row is null)
            return;
        _pinnedFight = row.Fight;
        FollowLive = false;
        RefreshGrid();
    }

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

    /// <summary>The bucket/ability drill table is on screen — the drill
    /// COLUMNS picker binds here, not to !SwingLevel, which left a stray
    /// no-op button on report and raw-log views.</summary>
    public bool DrillTableVisible => !SwingLevel && !LogLevel && !ReportLevel;

    partial void OnSwingLevelChanged(bool value)
    {
        OnPropertyChanged(nameof(SwingTableVisible));
        OnPropertyChanged(nameof(DrillTableVisible));
    }

    partial void OnLogLevelChanged(bool value)
    {
        OnPropertyChanged(nameof(SwingTableVisible));
        OnPropertyChanged(nameof(DrillTableVisible));
    }

    partial void OnReportLevelChanged(bool value)
    {
        OnPropertyChanged(nameof(SwingTableVisible));
        OnPropertyChanged(nameof(DrillTableVisible));
    }

    public ObservableCollection<AbilityRow> DrillRows { get; } = [];
    public BulkObservableCollection<SwingRow> SwingRows { get; } = [];
    public BulkObservableCollection<LogRow> LogRows { get; } = [];

    /// <summary>Deepest drill: click a swing to see the raw log around it,
    /// with the matching line highlighted and tokens colourised.</summary>
    [RelayCommand]
    private void OpenSwingLog(SwingRow? row)
    {
        if (row is null || string.IsNullOrEmpty(row.SourcePath))
            return;
        List<LogRow> logRows = [];
        var focusFound = false;
        foreach (var raw in LogWindowReader.Read(row.SourcePath, row.Epoch, beforeSeconds: 5, afterSeconds: 5))
        {
            var isFocus = false;
            if (!focusFound && LineEpoch(raw) == row.Epoch
                && (row.Amount <= 0 || LogLineHighlighter.ContainsAmount(raw, row.Amount))
                && (row.Ability == Core.Grammar.EnglishGrammar.AutoAttackAbility
                    || raw.Contains(row.Ability, StringComparison.Ordinal)))
            {
                isFocus = true;
                focusFound = true;
            }
            logRows.Add(new LogRow(LogLineHighlighter.Build(raw), isFocus));
        }
        LogRows.ReplaceAll(logRows);
        LogLevel = true;
        DrillChartVisible = false;
        ReportLevel = false;
        ReportChartVisible = false;
        _reportScope = 0;
    }

    private static long LineEpoch(string raw)
    {
        var close = raw.IndexOf(')');
        return close > 1 && long.TryParse(raw.AsSpan(1, close - 1), out var epoch) ? epoch : -1;
    }

    // ── Tree ────────────────────────────────────────────────────────────────

    private void RebuildTreeIfChanged()
    {
        long version;
        bool anyActive;
        lock (manager.Sync)
        {
            // Correlator.Version covers create/merge/delete/restore — a
            // History.Count signature was blind to in-place merges, leaving
            // stale tree labels after a second log joined a fight.
            version = manager.Correlator.Version;
            anyActive = manager.Sources.Any(s => s.Engine.InCombat);
        }
        if ((version, anyActive) == _treeSignature)
            return;
        _treeSignature = (version, anyActive);
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
            // "All" / "All Bosses" rollup nodes. The arrow collapses; the
            // header body selects the zone's encounter summary. The
            // Bosses-only filter trims trash fights.
            var groups = GroupHistoryZones();
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
                    Arrow = collapsed ? "▸" : "▾",
                    Title = $"{zoneName} - [{shown.Count}] {items[0].StartTime.ToLocalTime():HH:mm:ss}",
                    TitleBrush = ClassColors.TreeHeader,
                    IsDeletable = true,
                    GroupFights = [.. items],
                    Fight = new ZoneFights(zoneName, groupKey, [.. shown]),
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

        TreeNodes.ReplaceAll(nodes);
    }

    /// <summary>Trash mobs are articled ("a bloom custodian"); named bosses
    /// are not. Placeholder-titled scraps are never bosses.</summary>
    private static bool IsBossTitle(string title) => Encounter.IsBossTitle(title);

    /// <summary>Consecutive same-zone runs of history, oldest first — the
    /// shape behind both the tree and the zone-summary view. Callers hold
    /// the manager lock.</summary>
    private List<(string Zone, List<CorrelatedEncounter> Items)> GroupHistoryZones()
    {
        List<(string Zone, List<CorrelatedEncounter> Items)> groups = [];
        foreach (var fight in manager.Correlator.History)
        {
            if (groups.Count == 0 || !string.Equals(groups[^1].Zone, fight.Zone, StringComparison.OrdinalIgnoreCase))
                groups.Add((fight.Zone, []));
            groups[^1].Items.Add(fight);
        }
        return groups;
    }

    /// <summary>The zone group's current fights (respecting Bosses only) —
    /// re-resolved so a live zone's summary gains new fights as they end.
    /// Exact key match first; else membership (the key embeds the FIRST
    /// fight's start time, so deleting that fight drifts the key while the
    /// group lives on). A fully-deleted group resolves EMPTY — the old
    /// snapshot fallback resurrected deleted fights.</summary>
    private List<CorrelatedEncounter> ResolveZoneFights(ZoneFights zone)
    {
        List<CorrelatedEncounter>? match = null;
        foreach (var (z, items) in GroupHistoryZones())
        {
            var zoneName = string.IsNullOrEmpty(z) ? "Unknown zone" : z;
            if (string.Equals($"{zoneName}|{items[0].StartTime.Ticks}", zone.GroupKey, StringComparison.Ordinal))
            {
                match = items;
                break;
            }
            if (match is null
                && string.Equals(zoneName, zone.Zone, StringComparison.OrdinalIgnoreCase)
                && items.Any(zone.Snapshot.Contains))
                match = items;
        }
        if (match is null)
            return [];
        return BossesOnly ? [.. match.Where(f => IsBossTitle(f.Title))] : match;
    }

    /// <summary>One row per encounter: ally damage + deaths, enemy-side
    /// deaths as kills, bar scaled to the biggest fight's damage.</summary>
    private static List<ZoneFightRow> BuildZoneSummary(List<CorrelatedEncounter> fights)
    {
        List<(CorrelatedEncounter Fight, long Damage, int Kills, int Deaths)> stats = [];
        long top = 1;
        foreach (var fight in fights)
        {
            long damage = 0;
            int deaths = 0, kills = 0;
            foreach (var (key, entry) in fight.MergedCombatants)
            {
                if (fight.MergedAllyKeys.Contains(key))
                {
                    damage += entry.Combatant.Damage;
                    deaths += entry.Combatant.Deaths;
                }
                else
                {
                    kills += entry.Combatant.Deaths;
                }
            }
            stats.Add((fight, damage, kills, deaths));
            top = Math.Max(top, damage);
        }
        List<ZoneFightRow> rows = [];
        foreach (var (fight, damage, kills, deaths) in stats)
        {
            rows.Add(new ZoneFightRow(
                fight, fight.Title, OutcomeBrush(fight),
                fight.StartTime.ToLocalTime().ToString("HH:mm:ss"),
                FmtSpan(fight.Duration),
                CombatantRow.Compact(damage),
                CombatantRow.Compact(fight.EncDps),
                kills > 0 ? kills.ToString() : "",
                deaths > 0 ? deaths.ToString() : "",
                (double)damage / top));
        }
        return rows;
    }

    /// <summary>Every per-fight instance of one combatant across the three
    /// fight shapes (an aggregate yields one instance per member fight) —
    /// another shape-switch that was re-implemented per call site.</summary>
    private static List<Combatant> FightCombatantInstances(object fight, string key) => fight switch
    {
        Encounter e => e.Combatants.TryGetValue(key, out var c) ? [c] : [],
        CorrelatedEncounter m => m.MergedCombatants.TryGetValue(key, out var mc) ? [mc.Combatant] : [],
        AggregateFights a =>
            [.. a.Fights
                .Select(f => f.MergedCombatants.TryGetValue(key, out var mc) ? mc.Combatant : null)
                .Where(c => c is not null)
                .Select(c => c!)],
        _ => [],
    };

    /// <summary>Canonical fight duration in seconds (≥1) for rate maths —
    /// this shape-switch used to be re-implemented at every call site, and
    /// forgotten arms were a recurring bug class.</summary>
    private static double FightSeconds(object? fight) => Math.Max(1, fight switch
    {
        Encounter e => e.Duration.TotalSeconds,
        CorrelatedEncounter m => m.Duration.TotalSeconds,
        AggregateFights a => SumDuration(a.Fights).TotalSeconds,
        _ => 1.0,
    });

    private static TimeSpan SumDuration(IReadOnlyList<CorrelatedEncounter> fights)
    {
        var total = TimeSpan.Zero;
        foreach (var fight in fights)
            total += fight.Duration;
        return total;
    }

    private static string FmtSpan(TimeSpan span) =>
        span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");

}
