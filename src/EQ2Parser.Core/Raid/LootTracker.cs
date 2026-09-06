using System.Text.RegularExpressions;

namespace EQ2Parser.Core.Raid;

/// <summary>One dropped item in the current raid session.</summary>
public sealed class LootItemState
{
    public required int Id { get; init; }
    public required string ItemName { get; init; }
    public string? ChestType { get; init; }
    public string? Boss { get; set; }
    public DateTimeOffset DroppedAt { get; init; }

    /// <summary>Who picked it out of the chest (from the "X loots …" line).
    /// Null while it's still sitting in the chest.</summary>
    public string? LootedBy { get; set; }
}

/// <summary>
/// Raid loot accumulator (verbatim live shapes, mined from real logs
/// 2026-09):
///
///   Ariadneh opens Exquisite Chest and discovers:
///        \aITEM -842084919 1360811735:Porcupine II (Master)\/a
///        \aITEM 740343232 1125831452 0 0 0 2 1296083784:Black Unicorn Horn Wristlet\/a
///   Shadynecro loots \aITEM -479148241 279173422:Anaconda Scale Belt\/a from the Exquisite Chest of Sawtooth the Ancient.
///
/// A chest-open line starts a contents block; each indented ITEM line adds a
/// drop (duplicates are real — two of the same item drop). The block closes
/// on any non-item line or after <see cref="BlockTimeout"/>. A later
/// "loots … from the … Chest of …" line assigns the FIRST unassigned drop
/// with that item name (and back-fills the boss, which the chest-open line
/// doesn't carry); a loots line with no matching drop creates the item on
/// the spot (the logger may have missed the open). Corpse loot ("from the
/// corpse of") is trash and ignored entirely.
///
/// Thread-safe like RaidRosterTracker (lock + per-line timestamps); LIVE
/// lines only.
/// </summary>
public sealed partial class LootTracker
{
    public static readonly TimeSpan BlockTimeout = TimeSpan.FromSeconds(5);
    public const int MaxItems = 200;

    [GeneratedRegex(@"^(?<opener>[A-Za-z]+) opens (?<chest>[A-Za-z' ]+) and discovers: ?$")]
    private static partial Regex ChestOpenRegex();

    [GeneratedRegex(@"^\s+\\aITEM [-\d ]+:(?<name>[^\\]+)\\/a\s*$")]
    private static partial Regex ChestItemRegex();

    // The container must literally end in "Chest" — "from the corpse of X"
    // is trash loot and must never enter the list.
    [GeneratedRegex(@"^(?<looter>[A-Za-z]+) loots \\aITEM [-\d ]+:(?<name>[^\\]+)\\/a from the (?<chest>[A-Za-z' ]*Chest) of (?<boss>.+)\.$")]
    private static partial Regex LootsRegex();

    private readonly List<LootItemState> _items = [];
    private readonly object _gate = new();
    private int _nextId;
    private string? _openChestType;
    private DateTimeOffset _openAt;

    /// <summary>Raised (on the pump thread) whenever the loot list changes.</summary>
    public event Action? LootChanged;

    /// <summary>Cheap prefilter for the pump-thread hook.</summary>
    public static bool LooksRelevant(string message) =>
        message.Contains(@"\aITEM", StringComparison.Ordinal)
        || message.Contains(" and discovers:", StringComparison.Ordinal);

    /// <summary>Snapshot of every tracked item (copies — safe off-thread).</summary>
    public IReadOnlyList<LootItemState> Snapshot()
    {
        lock (_gate)
        {
            return [.. _items.Select(i => new LootItemState
            {
                Id = i.Id,
                ItemName = i.ItemName,
                ChestType = i.ChestType,
                Boss = i.Boss,
                DroppedAt = i.DroppedAt,
                LootedBy = i.LootedBy,
            })];
        }
    }

    public void StartNewSession()
    {
        lock (_gate)
        {
            _items.Clear();
            _openChestType = null;
            _nextId = 0;
        }
        LootChanged?.Invoke();
    }

    /// <summary>Feed one LIVE log line (any source).</summary>
    public void OnLine(string message, DateTimeOffset time)
    {
        var changed = false;
        lock (_gate)
        {
            if (_openChestType is not null && time - _openAt > BlockTimeout)
                _openChestType = null; // stale block

            if (ChestOpenRegex().Match(message) is { Success: true } open)
            {
                _openChestType = open.Groups["chest"].Value;
                _openAt = time;
            }
            else if (_openChestType is not null && ChestItemRegex().Match(message) is { Success: true } item)
            {
                changed = AddItem(item.Groups["name"].Value, _openChestType, time);
            }
            else if (LootsRegex().Match(message) is { Success: true } loots)
            {
                changed = AssignOrAdd(
                    loots.Groups["name"].Value,
                    loots.Groups["looter"].Value,
                    loots.Groups["chest"].Value,
                    loots.Groups["boss"].Value,
                    time);
                _openChestType = null; // a loots line means the contents block is over
            }
            else if (_openChestType is not null && !ChestItemRegex().IsMatch(message))
            {
                _openChestType = null; // any other line closes the block
            }
        }
        if (changed)
            LootChanged?.Invoke();
    }

    // ── internals (under _gate) ─────────────────────────────────────────────

    private bool AddItem(string name, string chestType, DateTimeOffset time)
    {
        if (_items.Count >= MaxItems)
            return false;
        _items.Add(new LootItemState
        {
            Id = _nextId++,
            ItemName = name.Trim(),
            ChestType = chestType,
            DroppedAt = time,
        });
        return true;
    }

    private bool AssignOrAdd(string name, string looter, string chestType, string boss, DateTimeOffset time)
    {
        name = name.Trim();
        var drop = _items.FirstOrDefault(i =>
            i.LootedBy is null && string.Equals(i.ItemName, name, StringComparison.OrdinalIgnoreCase));
        if (drop is null)
        {
            if (_items.Count >= MaxItems)
                return false;
            drop = new LootItemState
            {
                Id = _nextId++,
                ItemName = name,
                ChestType = chestType,
                DroppedAt = time,
            };
            _items.Add(drop);
        }
        drop.LootedBy = looter;
        drop.Boss ??= boss;
        return true;
    }
}
