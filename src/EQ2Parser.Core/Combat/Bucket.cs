namespace EQ2Parser.Core.Combat;

/// <summary>
/// A stat bucket (ACT's DamageTypeData): a named collection of per-ability
/// stats plus the synthetic "All" ability, with an ally-polarity value that
/// feeds the ally graph. The EQ2 bucket set and swing-category links are in
/// <see cref="BucketConfig"/>.
/// </summary>
public sealed class Bucket(string name, int allyValue)
{
    public const string AllAbility = "All";

    private readonly Dictionary<string, AbilityStats> _abilities = new(StringComparer.Ordinal);

    public string Name { get; } = name;

    /// <summary>+1 helpful, −1 hostile, 0 neutral — accumulated into the
    /// interaction graph between attacker and victim.</summary>
    public int AllyValue { get; } = allyValue;

    public IReadOnlyDictionary<string, AbilityStats> Abilities => _abilities;

    public AbilityStats All => GetOrCreate(AllAbility);

    public void Add(Swing swing)
    {
        GetOrCreate(AllAbility).Add(swing);
        GetOrCreate(swing.Ability).Add(swing);
    }

    private AbilityStats GetOrCreate(string ability)
    {
        if (!_abilities.TryGetValue(ability, out var stats))
            _abilities[ability] = stats = new AbilityStats(ability);
        return stats;
    }
}

/// <summary>The EQ2 default bucket universe (docs/act-behavior.md §3).
/// Compatibility-critical asymmetry: outgoing damage lands in TWO buckets
/// (its melee/non-melee split bucket AND the aggregate), incoming in one.</summary>
public static class BucketConfig
{
    public const string AutoAttackOut = "Auto-Attack (Out)";
    public const string SkillOut = "Skill/Ability (Out)";
    public const string OutgoingDamage = "Outgoing Damage";
    public const string HealedOut = "Healed (Out)";
    public const string PowerDrainOut = "Power Drain (Out)";
    public const string PowerReplenishOut = "Power Replenish (Out)";
    public const string CureOut = "Cure/Dispel (Out)";
    public const string ThreatOut = "Threat (Out)";
    public const string AllOutgoingRef = "All Outgoing (Ref)";

    public const string IncomingDamage = "Incoming Damage";
    public const string HealedInc = "Healed (Inc)";
    public const string PowerDrainInc = "Power Drain (Inc)";
    public const string PowerReplenishInc = "Power Replenish (Inc)";
    public const string CureInc = "Cure/Dispel (Inc)";
    public const string ThreatInc = "Threat (Inc)";
    public const string AllIncomingRef = "All Incoming (Ref)";

    public static readonly IReadOnlyDictionary<string, int> OutgoingAllyValues = new Dictionary<string, int>
    {
        [AutoAttackOut] = -1,
        [SkillOut] = -1,
        [OutgoingDamage] = 0,
        [HealedOut] = 1,
        [PowerDrainOut] = -1,
        [PowerReplenishOut] = 1,
        [CureOut] = 0,
        [ThreatOut] = -1,
        [AllOutgoingRef] = 0,
    };

    public static readonly IReadOnlyDictionary<string, int> IncomingAllyValues = new Dictionary<string, int>
    {
        [IncomingDamage] = -1,
        [HealedInc] = 1,
        [PowerDrainInc] = -1,
        [PowerReplenishInc] = 1,
        [CureInc] = 0,
        [ThreatInc] = -1,
        [AllIncomingRef] = 0,
    };

    public static readonly IReadOnlyDictionary<SwingCategory, string[]> OutgoingLinks = new Dictionary<SwingCategory, string[]>
    {
        [SwingCategory.Melee] = [AutoAttackOut, OutgoingDamage],
        [SwingCategory.NonMelee] = [SkillOut, OutgoingDamage],
        [SwingCategory.Healing] = [HealedOut],
        [SwingCategory.PowerDrain] = [PowerDrainOut],
        [SwingCategory.PowerReplenish] = [PowerReplenishOut],
        [SwingCategory.Threat] = [ThreatOut],
        [SwingCategory.CureDispel] = [CureOut],
    };

    public static readonly IReadOnlyDictionary<SwingCategory, string[]> IncomingLinks = new Dictionary<SwingCategory, string[]>
    {
        [SwingCategory.Melee] = [IncomingDamage],
        [SwingCategory.NonMelee] = [IncomingDamage],
        [SwingCategory.Healing] = [HealedInc],
        [SwingCategory.PowerDrain] = [PowerDrainInc],
        [SwingCategory.PowerReplenish] = [PowerReplenishInc],
        [SwingCategory.Threat] = [ThreatInc],
        [SwingCategory.CureDispel] = [CureInc],
    };
}
