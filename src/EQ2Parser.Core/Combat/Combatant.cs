namespace EQ2Parser.Core.Combat;

/// <summary>
/// One participant in an encounter, keyed by upper-case
/// name. Holds the outgoing/incoming buckets and the incremental ally
/// interaction graph. Stat definitions per docs/engine-behaviour.md §3.
/// </summary>
public sealed class Combatant(string name)
{
    /// <summary>Pseudo-ability name for kill/death bookkeeping swings.</summary>
    public const string KillingAbility = "Killing";

    /// <summary>The anonymous pseudo-combatant (dumbfire/ground-effect
    /// damage, unattributed deaths). Bookkeeping only — never an ally,
    /// never a correlation witness.</summary>
    public const string UnknownName = "Unknown";

    /// <summary>Combatant-dictionary key of <see cref="UnknownName"/>.</summary>
    public const string UnknownKey = "UNKNOWN";

    private readonly Dictionary<string, Bucket> _outgoing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bucket> _incoming = new(StringComparer.Ordinal);

    /// <summary>Display name (original casing of first sighting).</summary>
    public string Name { get; } = name;

    public string Key { get; } = name.ToUpperInvariant();

    /// <summary>Interaction polarity accumulated against other combatants
    /// (keyed upper-case). Input to <see cref="Encounter.GetAllies"/>.</summary>
    public SortedDictionary<string, int> Allies { get; } = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, Bucket> OutgoingBuckets => _outgoing;
    public IReadOnlyDictionary<string, Bucket> IncomingBuckets => _incoming;

    /// <summary>Attacker-side insertion: linked outgoing buckets + the
    /// reference bucket, accumulating ally polarity per linked bucket.</summary>
    public void AddOutgoing(Swing swing)
    {
        if (BucketConfig.OutgoingLinks.TryGetValue(swing.Category, out var buckets))
        {
            foreach (var bucketName in buckets)
            {
                ModAlly(swing.Victim, BucketConfig.OutgoingAllyValues[bucketName]);
                GetOrCreate(_outgoing, bucketName).Add(swing);
            }
        }
        GetOrCreate(_outgoing, BucketConfig.AllOutgoingRef).Add(swing);
        if (swing.Ability == Swing.AutoAttackAbility
            && swing.DamageType.Length > 0
            && swing.Damage.Number > 0)
        {
            _autoAttackBySchool.TryGetValue(swing.DamageType, out var acc);
            _autoAttackBySchool[swing.DamageType] = (acc.Damage + swing.Damage.Number, acc.Hits + 1);
        }
    }

    private readonly Dictionary<string, (long Damage, int Hits)> _autoAttackBySchool =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Landed auto-attack damage + hits per damage school
    /// ("piercing", "crushing", …). Feeds the Sources report's
    /// impossible-weapon check: a school the detected class cannot equip
    /// (<see cref="Analysis.MeleeCapabilities"/>) is a renamed pet's
    /// swings merged into this player.</summary>
    public IReadOnlyDictionary<string, (long Damage, int Hits)> AutoAttackBySchool => _autoAttackBySchool;

    /// <summary>Victim-side insertion (the mirror image).</summary>
    public void AddIncoming(Swing swing)
    {
        if (BucketConfig.IncomingLinks.TryGetValue(swing.Category, out var buckets))
        {
            foreach (var bucketName in buckets)
            {
                ModAlly(swing.Attacker, BucketConfig.IncomingAllyValues[bucketName]);
                GetOrCreate(_incoming, bucketName).Add(swing);
            }
        }
        GetOrCreate(_incoming, BucketConfig.AllIncomingRef).Add(swing);
    }

    private void ModAlly(string otherName, int mod)
    {
        // "Unknown" (either side) never participates in ally bookkeeping.
        if (mod == 0 || Name == UnknownName || otherName == UnknownName)
            return;
        var key = otherName.ToUpperInvariant();
        if (key == Key)
            return;
        Allies[key] = Allies.GetValueOrDefault(key) + mod;
    }

    private static Bucket GetOrCreate(Dictionary<string, Bucket> buckets, string name)
    {
        if (!buckets.TryGetValue(name, out var bucket))
            buckets[name] = bucket = new Bucket(name);
        return bucket;
    }

    private static AbilityStats? AllOf(Dictionary<string, Bucket> side, string bucketName) =>
        side.TryGetValue(bucketName, out var bucket) ? bucket.All : null;

    // ── Stat surface (each is the "All" ability of one bucket) ──────────────

    public long Damage => AllOf(_outgoing, BucketConfig.OutgoingDamage)?.Damage ?? 0;
    public long Healed => AllOf(_outgoing, BucketConfig.HealedOut)?.Damage ?? 0;
    public long DamageTaken => AllOf(_incoming, BucketConfig.IncomingDamage)?.Damage ?? 0;
    public long HealsTaken => AllOf(_incoming, BucketConfig.HealedInc)?.Damage ?? 0;
    public long PowerDamage => AllOf(_outgoing, BucketConfig.PowerDrainOut)?.Damage ?? 0;
    public long PowerReplenish => AllOf(_outgoing, BucketConfig.PowerReplenishOut)?.Damage ?? 0;
    public int CureDispels => AllOf(_outgoing, BucketConfig.CureOut)?.SwingCount ?? 0;

    /// <summary>How many swings this source observed for this combatant
    /// (both directions) — the multi-log correlator's coverage measure for
    /// choosing the authoritative source per combatant.</summary>
    public int ObservedSwingCount =>
        (AllOf(_outgoing, BucketConfig.AllOutgoingRef)?.Swings.Count ?? 0)
        + (AllOf(_incoming, BucketConfig.AllIncomingRef)?.Swings.Count ?? 0);

    /// <summary>Personal window start: the first outgoing action of any kind.</summary>
    public DateTimeOffset StartTime => AllOf(_outgoing, BucketConfig.AllOutgoingRef)?.StartTime ?? DateTimeOffset.MaxValue;

    /// <summary>Personal window end: the last outgoing action of any kind.</summary>
    public DateTimeOffset EndTime => AllOf(_outgoing, BucketConfig.AllOutgoingRef)?.EndTime ?? DateTimeOffset.MinValue;

    /// <summary>The last DAMAGING outgoing swing — feeds the encounter's
    /// default end time (trailing heals never extend an encounter).</summary>
    public DateTimeOffset ShortEndTime => AllOf(_outgoing, BucketConfig.OutgoingDamage)?.EndTime ?? DateTimeOffset.MinValue;

    /// <summary>Personal duration (single-segment).</summary>
    public TimeSpan Duration
    {
        get
        {
            var start = StartTime;
            var end = EndTime;
            return end > start ? end - start : TimeSpan.Zero;
        }
    }

    /// <summary>Marks a Killing swing as a TEMP PET dying under its owner's
    /// name (the Templar hammer logs "X has killed Menludiir" — third person,
    /// owner's own name, in the owner's own log; a REAL self-death is always
    /// second person "has killed you"). Excluded from Deaths so a raiding
    /// Templar's count isn't ~6× inflated by counting pet-hammer expiries.</summary>
    public const string PetDeathExtra = "pet-death";

    /// <summary>Deaths: the incoming Killing pseudo-ability count (pet
    /// deaths excluded), else death-coded incoming swings.</summary>
    public int Deaths
    {
        get
        {
            if (_incoming.TryGetValue(BucketConfig.AllIncomingRef, out var reference)
                && reference.Abilities.TryGetValue(KillingAbility, out var killing))
                return killing.Swings.Count(sw => sw.Extra != PetDeathExtra);
            return AllOf(_incoming, BucketConfig.AllIncomingRef)?.DeathCount ?? 0;
        }
    }

    /// <summary>Temp-pet deaths recorded under this combatant's name —
    /// shown separately, never counted as the player dying.</summary>
    public int PetDeaths
    {
        get
        {
            if (_incoming.TryGetValue(BucketConfig.AllIncomingRef, out var reference)
                && reference.Abilities.TryGetValue(KillingAbility, out var killing))
                return killing.Swings.Count(sw => sw.Extra == PetDeathExtra);
            return 0;
        }
    }

    /// <summary>Kill credit. Non-allies only get credit for player-shaped
    /// (space-free) victim names; the encounter passes that flag in.</summary>
    public int GetKills(bool isAlly)
    {
        var all = AllOf(_outgoing, BucketConfig.AllOutgoingRef);
        if (all is null)
            return 0;
        var count = 0;
        foreach (var s in all.Swings)
            if (s.Damage.IsDeath && s.Extra != PetDeathExtra
                && (isAlly || Swing.LooksLikePlayer(s.Victim)))
                count++;
        return count;
    }
}
