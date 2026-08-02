namespace EQ2Parser.Core.Combat;

/// <summary>
/// Per-ability swing list + derived statistics (ACT's AttackType), with the
/// exact counting rules from docs/act-behavior.md §3:
///   * Hits count zero-damage swings ("block is hit" default behaviour),
///   * Misses are the string-sensitive Miss equality,
///   * Avoids are codes −2…−9 excluding Death,
///   * Median is the upper median of non-negative amounts.
/// Aggregates update incrementally in <see cref="Add"/> — every getter used
/// to rescan the swing list, and the UI tick reads them per combatant at
/// 10 Hz, which made long fights O(combatants × total swings) per tick.
/// Swings are append-only, so the running totals are always exact.
/// </summary>
public sealed class AbilityStats(string name)
{
    private readonly List<Swing> _swings = [];
    private long _damage;
    private long _maxHit;
    private long _minHit = long.MaxValue;
    private int _hits;
    private int _critHits;
    private int _misses;
    private int _avoids;
    private int _nonDeath;
    private int _deaths;
    private DateTimeOffset _start = DateTimeOffset.MaxValue;
    private DateTimeOffset _end = DateTimeOffset.MinValue;

    public string Name { get; } = name;
    public IReadOnlyList<Swing> Swings => _swings;

    public void Add(Swing swing)
    {
        _swings.Add(swing);
        var number = swing.Damage.Number;
        if (number > 0)
        {
            _damage += number;
            if (number > _maxHit)
                _maxHit = number;
        }
        if (number >= 0)
        {
            _hits++;
            if (swing.Critical)
                _critHits++;
            if (number < _minHit)
                _minHit = number;
        }
        if (swing.Damage.IsDeath)
            _deaths++;
        else
            _nonDeath++;
        if (swing.Damage.IsMiss)
            _misses++;
        if (number < DamageValue.MissNumber && !swing.Damage.Equals(DamageValue.Death))
            _avoids++;
        if (swing.Time < _start)
            _start = swing.Time;
        if (swing.Time > _end)
            _end = swing.Time;
    }

    public long Damage => _damage;

    /// <summary>Swing count excluding deaths (the "Killing" pseudo-ability
    /// counts them — the engine names it <c>Killing</c>).</summary>
    public int SwingCount => Name == Combatant.KillingAbility ? _swings.Count : _nonDeath;

    public int Hits => _hits;
    public int CritHits => _critHits;
    public int Misses => _misses;

    /// <summary>Resist/Parry/Riposte/Block/Unknown — everything below Miss
    /// except Death (string-sensitive, matching ACT's Dnum inequality).</summary>
    public int Avoids => _avoids;

    /// <summary>Death-coded swings — the O(1) source for death counting.</summary>
    public int DeathCount => _deaths;

    public long MaxHit => _maxHit;

    public long MinHit => _minHit == long.MaxValue ? 0 : _minHit;

    /// <summary>Upper median of the non-negative amounts; 0 when empty.
    /// Still on-demand — it needs the sorted distribution, and only the
    /// occasional report reads it.</summary>
    public long Median
    {
        get
        {
            var amounts = new List<long>();
            foreach (var s in _swings)
                if (s.Damage.Number >= 0)
                    amounts.Add(s.Damage.Number);
            if (amounts.Count == 0)
                return 0;
            amounts.Sort();
            return amounts[amounts.Count / 2];
        }
    }

    public DateTimeOffset StartTime => _start;

    public DateTimeOffset EndTime => _end;
}
