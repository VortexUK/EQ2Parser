namespace EQ2Parser.Core.Combat;

/// <summary>
/// Per-ability swing list + derived statistics (ACT's AttackType), with the
/// exact counting rules from docs/act-behavior.md §3:
///   * Hits count zero-damage swings ("block is hit" default behaviour),
///   * Misses are the string-sensitive Miss equality,
///   * Avoids are codes −2…−9 excluding Death,
///   * Median is the upper median of non-negative amounts.
/// </summary>
public sealed class AbilityStats(string name)
{
    private readonly List<Swing> _swings = [];

    public string Name { get; } = name;
    public IReadOnlyList<Swing> Swings => _swings;

    public void Add(Swing swing) => _swings.Add(swing);

    public long Damage
    {
        get
        {
            long sum = 0;
            foreach (var s in _swings)
                if (s.Damage.Number > 0)
                    sum += s.Damage.Number;
            return sum;
        }
    }

    /// <summary>Swing count excluding deaths (the "Killing" pseudo-ability
    /// counts them — the engine names it <c>Killing</c>).</summary>
    public int SwingCount
    {
        get
        {
            if (Name == Combatant.KillingAbility)
                return _swings.Count;
            var count = 0;
            foreach (var s in _swings)
                if (!s.Damage.IsDeath)
                    count++;
            return count;
        }
    }

    public int Hits => Count(static s => s.Damage.Number >= 0);
    public int CritHits => Count(static s => s.Critical && s.Damage.Number >= 0);
    public int Misses => Count(static s => s.Damage.IsMiss);

    /// <summary>Resist/Parry/Riposte/Block/Unknown — everything below Miss
    /// except Death (string-sensitive, matching ACT's Dnum inequality).</summary>
    public int Avoids => Count(static s => s.Damage.Number < DamageValue.MissNumber && !s.Damage.Equals(DamageValue.Death));

    public long MaxHit
    {
        get
        {
            long max = 0;
            foreach (var s in _swings)
                if (s.Damage.Number > max)
                    max = s.Damage.Number;
            return max;
        }
    }

    public long MinHit
    {
        get
        {
            long min = long.MaxValue;
            foreach (var s in _swings)
                if (s.Damage.Number >= 0 && s.Damage.Number < min)
                    min = s.Damage.Number;
            return min == long.MaxValue ? 0 : min;
        }
    }

    /// <summary>Upper median of the non-negative amounts; 0 when empty.</summary>
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

    public DateTimeOffset StartTime
    {
        get
        {
            var min = DateTimeOffset.MaxValue;
            foreach (var s in _swings)
                if (s.Time < min)
                    min = s.Time;
            return min;
        }
    }

    public DateTimeOffset EndTime
    {
        get
        {
            var max = DateTimeOffset.MinValue;
            foreach (var s in _swings)
                if (s.Time > max)
                    max = s.Time;
            return max;
        }
    }

    private int Count(Func<Swing, bool> predicate)
    {
        var count = 0;
        foreach (var s in _swings)
            if (predicate(s))
                count++;
        return count;
    }
}
