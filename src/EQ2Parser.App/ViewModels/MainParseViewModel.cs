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
    /// <summary>Encounter (live), CorrelatedEncounter (history), or
    /// AggregateFights ("All" / "All Bosses" zone rollups).</summary>
    public object? Fight { get; init; }
}

/// <summary>A zone rollup selection: combined stats over several fights.</summary>
public sealed record AggregateFights(string Zone, string Label, IReadOnlyList<CorrelatedEncounter> Fights);

/// <summary>One row of a drill-down table (ability or attacker breakdown).</summary>
public sealed partial class AbilityRow : ObservableObject
{
    public required string Key { get; init; }

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
    private double _barFraction;
}

/// <summary>One swing of the deepest drill level.</summary>
public sealed record SwingRow(string Time, string Result, string Crit, string Special, string Type, string Other);

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

    public ObservableCollection<AbilityRow> DrillRows { get; } = [];
    public ObservableCollection<SwingRow> SwingRows { get; } = [];

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
        if (row is null)
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
            // style ("The Emerald Halls - [25] 18:57:04") with per-zone
            // "All" / "All Bosses" rollup nodes.
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
                nodes.Add(new ParseNode
                {
                    IsHeader = true,
                    Title = $"{zoneName} - [{items.Count}] {items[0].StartTime.ToLocalTime():HH:mm:ss}",
                });
                if (items.Count > 1)
                {
                    var all = items.ToArray();
                    nodes.Add(new ParseNode
                    {
                        Title = $"All - [{FmtSpan(SumDuration(all))}]",
                        Fight = new AggregateFights(zoneName, "All", all),
                    });
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
                for (var i = items.Count - 1; i >= 0; i--)
                {
                    var fight = items[i];
                    var sources = fight.Sources.Count > 1 ? $" ·{fight.Sources.Count}L" : "";
                    nodes.Add(new ParseNode
                    {
                        Title = $"{fight.Title} - [{FmtSpan(fight.Duration)}] {fight.StartTime.ToLocalTime():HH:mm:ss}{sources}",
                        Fight = fight,
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

        lock (manager.Sync)
        {
            var fight = ResolveFight();
            if (fight is null)
                return;
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
        }

        Breadcrumb = breadcrumb;
        InCombat = live;
        PetHeader = $"Pets ({pets.Count})";
        Apply(AllyRows, Sort(allies));
        Apply(PetRows, Sort(pets));
        Apply(EnemyRows, Sort(enemies));

        if (detail is not null)
        {
            DetailTitle = detail.Title;
            DrillNameHeader = detail.NameHeader;
            SwingLevel = detail.Swings is not null;
            if (detail.Table is not null)
                ApplyAbilityRows(DrillRows, detail.Table, sort: detail.SortTable);
            if (detail.Swings is not null)
            {
                SwingRows.Clear();
                foreach (var swing in detail.Swings)
                    SwingRows.Add(swing);
            }
        }
    }

    // ── Drill-down snapshot ─────────────────────────────────────────────────

    private sealed record AbilityData(string Name, string Source, int Swings, int Hits, int Crits, long Max, long Total);

    private sealed record DetailData(string Title, string NameHeader, bool SortTable, List<AbilityData>? Table, List<SwingRow>? Swings);

    private sealed class AbilityAcc
    {
        public int Swings;
        public int Hits;
        public int Crits;
        public long Max;
        public long Total;
    }

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
        List<Combatant> instances = fight switch
        {
            Encounter encounter =>
                encounter.Combatants.TryGetValue(key, out var c) ? [c] : [],
            CorrelatedEncounter merged =>
                merged.MergedCombatants.TryGetValue(key, out var mc) ? [mc.Combatant] : [],
            AggregateFights aggregate =>
                [.. aggregate.Fights
                    .Select(f => f.MergedCombatants.TryGetValue(key, out var mc) ? mc.Combatant : null)
                    .Where(c => c is not null)
                    .Select(c => c!)],
            _ => [],
        };
        if (instances.Count == 0)
            return null;

        var name = instances[0].Name;
        var detection = manager.Classifier.Identifier.Detect(instances[0]);
        var cls = detection.ClassName is not null ? $" · {detection.ClassName}" : "";

        // Depth 1 — the combatant's buckets, canonical ACT order.
        if (_detailBucket is null)
        {
            List<AbilityData> table = [];
            foreach (var bucketName in BucketOrder)
            {
                var acc = new AbilityAcc();
                foreach (var combatant in instances)
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
                    table.Add(new AbilityData(bucketName, "", acc.Swings, acc.Hits, acc.Crits, acc.Max, acc.Total));
            }
            return new DetailData($"{name}{cls}", "BUCKET", SortTable: false, table, null);
        }

        // Depth 2 — abilities within the chosen bucket.
        if (_detailAbility is null)
        {
            var abilities = new Dictionary<string, AbilityAcc>(StringComparer.Ordinal);
            foreach (var combatant in instances)
            {
                if (FindBucket(combatant, _detailBucket) is not { } bucket)
                    continue;
                foreach (var (abilityName, stats) in bucket.Abilities)
                {
                    if (abilityName == Bucket.AllAbility)
                        continue;
                    var acc = GetOrAdd(abilities, abilityName);
                    acc.Swings += stats.SwingCount;
                    acc.Hits += stats.Hits;
                    acc.Crits += stats.CritHits;
                    acc.Max = Math.Max(acc.Max, stats.MaxHit);
                    acc.Total += stats.Damage;
                }
            }
            var classify = !IsIncomingBucket(_detailBucket);
            List<AbilityData> table = [.. abilities.Select(kv => new AbilityData(
                kv.Key,
                classify
                    ? manager.Classifier.Identifier.ClassifySource(kv.Key, detection.ClassName)
                        .ToString().ToLowerInvariant()
                    : "",
                kv.Value.Swings, kv.Value.Hits, kv.Value.Crits, kv.Value.Max, kv.Value.Total))];
            return new DetailData($"{name}{cls} › {_detailBucket}", "ABILITY", SortTable: true, table, null);
        }

        // Depth 3 — the individual swings of one ability.
        var title = $"{name}{cls} › {_detailBucket} › {_detailAbility}";
        var incoming = IsIncomingBucket(_detailBucket);
        List<Core.Combat.Swing> collected = [];
        foreach (var combatant in instances)
        {
            if (FindBucket(combatant, _detailBucket) is not { } bucket)
                continue;
            if (bucket.Abilities.TryGetValue(_detailAbility, out var stats))
                collected.AddRange(stats.Swings);
        }
        var signature = (key, _detailBucket, _detailAbility, collected.Count);
        if (signature == _swingSignature && SwingRows.Count > 0)
            return new DetailData(title, "ABILITY", SortTable: false, null, null);
        _swingSignature = signature;

        collected.Sort((a, b) =>
        {
            var byTime = a.Time.CompareTo(b.Time);
            return byTime != 0 ? byTime : a.TimeSorter.CompareTo(b.TimeSorter);
        });
        List<SwingRow> swings = [.. collected.Select(s => new SwingRow(
            s.Time.ToLocalTime().ToString("HH:mm:ss"),
            s.Damage.ToString(),
            s.Critical ? "crit" : "",
            s.Special == "None" ? "" : s.Special,
            s.DamageType,
            incoming ? s.Attacker : s.Victim))];
        return new DetailData(title, "ABILITY", SortTable: false, null, swings);
    }

    private static AbilityAcc GetOrAdd(Dictionary<string, AbilityAcc> accs, string key)
    {
        if (!accs.TryGetValue(key, out var acc))
            accs[key] = acc = new AbilityAcc();
        return acc;
    }

    private static void ApplyAbilityRows(ObservableCollection<AbilityRow> rows, List<AbilityData> snapshot, bool sort)
    {
        if (sort)
            snapshot.Sort((a, b) => b.Total.CompareTo(a.Total));
        var top = snapshot.Count > 0 ? Math.Max(1, snapshot.Max(r => r.Total)) : 1;
        var total = Math.Max(1, snapshot.Sum(r => r.Total));
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
            row.Name = data.Name;
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
            row.BarFraction = (double)data.Total / top;
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
