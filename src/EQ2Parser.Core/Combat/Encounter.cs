namespace EQ2Parser.Core.Combat;

/// <summary>Encounter outcome, ACT-compatible (docs/act-behavior.md §2):
/// 0 indeterminate, 1 = enemy died AND an ally survived, 2 = exactly one of
/// those, 3 = neither.</summary>
public enum SuccessLevel
{
    Indeterminate = 0,
    Win = 1,
    Partial = 2,
    Loss = 3,
}

/// <summary>
/// One fight (ACT's EncounterData): combatants keyed by upper-case name,
/// Start/End time segments, and the derived encounter-level stats. Stat and
/// lifecycle semantics per docs/act-behavior.md §§2-3. Source-aware: carries
/// the id of the log source that produced it (multi-log correlation groups
/// encounters across sources later).
/// </summary>
public sealed class Encounter(string sourceId, string ownerName, string zone)
{
    public const string PlaceholderTitle = "Encounter";

    private readonly Dictionary<string, Combatant> _combatants = new(StringComparer.Ordinal);
    private IReadOnlyList<Combatant>? _alliesCache;

    /// <summary>Which log source produced this encounter (multi-log).</summary>
    public string SourceId { get; } = sourceId;

    /// <summary>The log owner's character name — seeds ally resolution.</summary>
    public string OwnerName { get; } = ownerName;

    public string Zone { get; } = zone;

    /// <summary>Segment starts. [0] is stamped by the first swing; further
    /// segments come from silence cutting (not yet implemented).</summary>
    public List<DateTimeOffset> StartTimes { get; } = [];
    public List<DateTimeOffset> EndTimes { get; } = [];

    public bool Active { get; private set; }
    public string Title { get; private set; } = PlaceholderTitle;

    public IReadOnlyDictionary<string, Combatant> Combatants => _combatants;

    public void AddSwing(Swing swing)
    {
        if (!Active)
        {
            StartTimes.Add(swing.Time);
            Active = true;
        }
        _alliesCache = null;
        // Self-damage (lifetap procs like Vampiric Requiem hitting the
        // caster) records on the TAKEN side only — ACT never credits it as
        // outgoing, or it would inflate the attacker's own DPS. Self-heals
        // and self-buffs still count as outgoing.
        var selfDamage = swing.Category is SwingCategory.Melee or SwingCategory.NonMelee
            && string.Equals(swing.Attacker, swing.Victim, StringComparison.OrdinalIgnoreCase);
        if (!selfDamage)
            GetOrCreate(swing.Attacker).AddOutgoing(swing);
        GetOrCreate(swing.Victim).AddIncoming(swing);
    }

    private Combatant GetOrCreate(string name)
    {
        var key = name.ToUpperInvariant();
        if (!_combatants.TryGetValue(key, out var combatant))
            _combatants[key] = combatant = new Combatant(name.Trim());
        return combatant;
    }

    /// <summary>Finalize: close the last segment and assign the real title.</summary>
    public void End()
    {
        if (!Active)
            return;
        Active = false;
        var end = EndTime;
        // Never record a segment that ends before it starts.
        EndTimes.Add(end > StartTimes[^1] ? end : StartTimes[^1]);
        Title = GetStrongestEnemy() ?? PlaceholderTitle;
    }

    // ── Windows and duration ────────────────────────────────────────────────

    /// <summary>Earliest outgoing action by anyone.</summary>
    public DateTimeOffset StartTime
    {
        get
        {
            var min = DateTimeOffset.MaxValue;
            foreach (var c in _combatants.Values)
                if (c.StartTime < min)
                    min = c.StartTime;
            return min;
        }
    }

    /// <summary>Default encounter end: the last damaging swing by an ALLY
    /// (falls back to all combatants when the ally set is empty).</summary>
    public DateTimeOffset EndTime
    {
        get
        {
            var pool = GetAllies();
            IEnumerable<Combatant> candidates = pool.Count > 0 ? pool : _combatants.Values;
            var max = DateTimeOffset.MinValue;
            foreach (var c in candidates)
                if (c.ShortEndTime > max)
                    max = c.ShortEndTime;
            return max;
        }
    }

    /// <summary>Sum of closed segments; the live segment uses the current
    /// <see cref="EndTime"/>.</summary>
    public TimeSpan Duration
    {
        get
        {
            if (StartTimes.Count == 0)
                return TimeSpan.Zero;
            var total = TimeSpan.Zero;
            for (var i = 0; i < StartTimes.Count; i++)
            {
                var end = i < EndTimes.Count ? EndTimes[i] : EndTime;
                if (end > StartTimes[i])
                    total += end - StartTimes[i];
            }
            return total;
        }
    }

    // ── Encounter-level stats (allies only) ─────────────────────────────────

    public long Damage => SumAllies(static c => c.Damage);
    public long Healed => SumAllies(static c => c.Healed);

    public double EncDps => Rate(Damage);

    private double Rate(long amount)
    {
        var seconds = Duration.TotalSeconds;
        return seconds > 0 ? amount / seconds : 0;
    }

    private long SumAllies(Func<Combatant, long> selector)
    {
        long sum = 0;
        foreach (var ally in GetAllies())
            sum += selector(ally);
        return sum;
    }

    /// <summary>Per-combatant EncDPS — damage over the ENCOUNTER duration
    /// (this is the number uploads and rankings use).</summary>
    public double EncDpsOf(Combatant combatant) => Rate(combatant.Damage);

    public double EncHpsOf(Combatant combatant) => Rate(combatant.Healed);

    // ── Allies (sign-propagation over the interaction graph) ────────────────

    /// <summary>
    /// ACT-compatible ally resolution: seed the owner at 0, repeatedly walk
    /// every discovered node's interaction entries — a node with positive
    /// value adds each entry's polarity to the other party, a non-positive
    /// node subtracts it — until no new node appears. Allies are the nodes
    /// whose final sign matches the owner's (exact zeros excluded).
    /// The sign convention is emergent, not fixed. An encounter that does not
    /// contain the owner has NO allies (⇒ zero damage, placeholder title,
    /// indeterminate success).
    /// </summary>
    public IReadOnlyList<Combatant> GetAllies()
    {
        if (_alliesCache is not null)
            return _alliesCache;

        var ownerKey = OwnerName.ToUpperInvariant();
        if (!_combatants.ContainsKey(ownerKey))
            return _alliesCache = [];

        var values = new Dictionary<string, int>(StringComparer.Ordinal) { [ownerKey] = 0 };
        var discovered = true;
        while (discovered)
        {
            discovered = false;
            foreach (var nodeKey in values.Keys.ToArray())
            {
                var node = _combatants[nodeKey];
                foreach (var (otherKey, mod) in node.Allies)
                {
                    if (!_combatants.ContainsKey(otherKey))
                        continue;
                    if (!values.ContainsKey(otherKey))
                    {
                        values[otherKey] = 0;
                        discovered = true;
                    }
                    if (values[nodeKey] > 0)
                        values[otherKey] += mod;
                    else
                        values[otherKey] -= mod;
                }
            }
        }

        var ownerNegative = values[ownerKey] < 0;
        var allies = new List<Combatant>();
        foreach (var (key, value) in values)
        {
            if (value == 0 && key != ownerKey)
                continue;
            var sameSign = ownerNegative ? value < 0 : value > 0;
            // The owner is always their own ally even when their value is 0.
            if (sameSign || key == ownerKey)
                allies.Add(_combatants[key]);
        }
        return _alliesCache = allies;
    }

    // ── Title + outcome ─────────────────────────────────────────────────────

    /// <summary>Named mob = boss: EQ2 trash carries an article ("a vampire
    /// thrall"), named encounters don't ("Mayong Mistmoore"). Shared by the
    /// tree filter and history retention so they never disagree.</summary>
    public static bool IsBossTitle(string title) =>
        title != PlaceholderTitle
        && !title.StartsWith("a ", StringComparison.Ordinal)
        && !title.StartsWith("an ", StringComparison.Ordinal);

    public bool IsBossFight => IsBossTitle(Title);

    /// <summary>The non-ally that soaked the most damage per death
    /// (integer division), i.e. what the fight was "about".</summary>
    public string? GetStrongestEnemy()
    {
        var allies = GetAllies();
        if (allies.Count == 0)
            return null;
        var allyKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ally in allies)
            allyKeys.Add(ally.Key);

        Combatant? strongest = null;
        long strongestScore = -1;
        foreach (var c in _combatants.Values)
        {
            if (allyKeys.Contains(c.Key))
                continue;
            var deaths = c.Deaths;
            var score = deaths <= 0 ? c.DamageTaken : c.DamageTaken / deaths;
            if (score > strongestScore)
            {
                strongestScore = score;
                strongest = c;
            }
        }
        return strongest?.Name;
    }

    public SuccessLevel GetSuccessLevel()
    {
        var allies = GetAllies();
        if (allies.Count == 0)
            return SuccessLevel.Indeterminate;
        var enemyName = GetStrongestEnemy();
        if (enemyName is null)
            return SuccessLevel.Indeterminate;

        var enemyDied = _combatants[enemyName.ToUpperInvariant()].Deaths > 0;
        var allySurvived = false;
        foreach (var ally in allies)
        {
            if (ally.Deaths == 0 && Swing.LooksLikePlayer(ally.Name))
            {
                allySurvived = true;
                break;
            }
        }

        return (enemyDied, allySurvived) switch
        {
            (true, true) => SuccessLevel.Win,
            (false, false) => SuccessLevel.Loss,
            _ => SuccessLevel.Partial,
        };
    }
}
