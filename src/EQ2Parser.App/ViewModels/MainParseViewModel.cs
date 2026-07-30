using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;

namespace EQ2Parser.App.ViewModels;

/// <summary>One entry in the left-hand parse tree: a zone header or a fight.</summary>
public sealed class ParseNode
{
    public bool IsHeader { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>Encounter (live) or CorrelatedEncounter (history).</summary>
    public object? Fight { get; init; }
}

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
    public ObservableCollection<CombatantRow> EnemyRows { get; } = [];

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

    partial void OnSelectedNodeChanged(ParseNode? value)
    {
        if (value?.Fight is null)
            return;
        _pinnedFight = value.Fight;
        FollowLive = false;
        RefreshGrid();
    }

    [RelayCommand]
    private void ResumeLive()
    {
        FollowLive = true;
        _pinnedFight = null;
        RefreshGrid();
    }

    [RelayCommand]
    private void EndCombat()
    {
        lock (manager.Sync)
        {
            foreach (var source in manager.Sources)
                source.Engine.EndCombat();
        }
        Refresh();
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

        List<ParseNode> nodes = [];
        lock (manager.Sync)
        {
            // Newest first: group consecutive same-zone fights, ACT-sidebar
            // style ("The Emerald Halls - [25] 18:57:04").
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
                nodes.Add(new ParseNode
                {
                    IsHeader = true,
                    Title = string.IsNullOrEmpty(zone) ? "Unknown zone" : zone,
                    Detail = $"[{items.Count}]  {items[0].StartTime.ToLocalTime():HH:mm:ss}",
                });
                for (var i = items.Count - 1; i >= 0; i--)
                {
                    var fight = items[i];
                    var sources = fight.Sources.Count > 1 ? $"  ·  {fight.Sources.Count} logs" : "";
                    nodes.Add(new ParseNode
                    {
                        Title = fight.Title,
                        Detail = $"{fight.StartTime.ToLocalTime():HH:mm:ss}  ·  {fight.Duration.TotalSeconds:F0}s{sources}",
                        Fight = fight,
                    });
                }
            }
        }

        TreeNodes.Clear();
        foreach (var node in nodes)
            TreeNodes.Add(node);
    }

    // ── Grid ────────────────────────────────────────────────────────────────

    private sealed record RowData(
        string Key, string Name, string Cls, System.Windows.Media.Brush Brush, bool IsPet,
        double Seconds, long Damage, double Dps, double Hps, long Taken, int Deaths);

    private void RefreshGrid()
    {
        List<RowData> allies = [];
        List<RowData> enemies = [];
        string breadcrumb;
        var live = false;

        lock (manager.Sync)
        {
            var fight = ResolveFight();
            if (fight is null)
                return;

            switch (fight)
            {
                case Encounter encounter:
                    live = encounter.Active;
                    breadcrumb = Describe(encounter.Zone, encounter.Title, encounter.Duration, encounter.EncDps, live);
                    SnapshotEncounter(encounter, allies, enemies);
                    break;
                case CorrelatedEncounter merged:
                    breadcrumb = Describe(merged.Zone, merged.Title, merged.Duration, merged.EncDps, live: false);
                    SnapshotMerged(merged, allies, enemies);
                    break;
                default:
                    return;
            }
        }

        Breadcrumb = breadcrumb;
        InCombat = live;
        Apply(AllyRows, Sort(allies));
        Apply(EnemyRows, Sort(enemies));
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

    private void SnapshotEncounter(Encounter encounter, List<RowData> allies, List<RowData> enemies)
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
            (tag.Kind == CombatantKind.Enemy ? enemies : allies).Add(row);
        }
    }

    private void SnapshotMerged(CorrelatedEncounter merged, List<RowData> allies, List<RowData> enemies)
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
            (tag.Kind == CombatantKind.Enemy ? enemies : allies).Add(row);
        }
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

    private static void Apply(ObservableCollection<CombatantRow> rows, List<RowData> snapshot)
    {
        var top = snapshot.Count > 0 ? Math.Max(1, snapshot.Max(r => r.Damage)) : 1;
        var total = Math.Max(1, snapshot.Sum(r => r.Damage));
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
            row.Name = data.Name;
            row.ClassName = data.Cls;
            row.ClassBrush = data.Brush;
            row.IsPet = data.IsPet;
            row.Duration = TimeSpan.FromSeconds(data.Seconds).ToString(@"mm\:ss");
            row.Damage = CombatantRow.Compact(data.Damage);
            row.Percent = $"{100.0 * data.Damage / total:F0}%";
            row.Dps = CombatantRow.Compact(data.Dps);
            row.Hps = data.Hps > 0 ? CombatantRow.Compact(data.Hps) : "";
            row.Taken = data.Taken > 0 ? CombatantRow.Compact(data.Taken) : "";
            row.Deaths = data.Deaths > 0 ? data.Deaths.ToString() : "";
            row.BarFraction = (double)data.Damage / top;
        }
        while (rows.Count > snapshot.Count)
            rows.RemoveAt(rows.Count - 1);
    }
}
