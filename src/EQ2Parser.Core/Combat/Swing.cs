namespace EQ2Parser.Core.Combat;

/// <summary>ACT-compatible swing categories. The numeric values are part of
/// the compatibility surface (docs/act-behavior.md §3); categories without a
/// bucket link contribute only to the reference buckets.</summary>
public enum SwingCategory
{
    Melee = 1,
    NonMelee = 2,
    Healing = 3,
    PowerDrain = 10,
    PowerReplenish = 13,
    Threat = 16,
    CureDispel = 20,

    /// <summary>Control-effect apply/release lines ("X is stunned!" /
    /// "X is no longer stunned."). Ours, not ACT's — files only into the
    /// Ref buckets (no damage stats), archived for CC-kind mining.</summary>
    StatusEffect = 30,
}

/// <summary>
/// One combat action (ACT's MasterSwing). Immutable once created; grammar
/// normalisation (trimming, Special defaulting) happens in the engine before
/// construction.
/// </summary>
/// <param name="Category">Swing category — selects the stat buckets.</param>
/// <param name="Critical">Whether the action was a critical.</param>
/// <param name="Special">Modifier tag (flurry, multi attack, …); "None" when absent.</param>
/// <param name="Attacker">Actor name (trimmed; upper-cased for keying by consumers).</param>
/// <param name="Ability">Ability/spell name; also the spell-timer match key.</param>
/// <param name="Damage">Amount or sentinel code.</param>
/// <param name="Time">Log timestamp (1 s granularity).</param>
/// <param name="TimeSorter">Monotonic per-line sequence — disambiguates same-second ordering.</param>
/// <param name="Victim">Target name.</param>
/// <param name="DamageType">Damage school / sub-classification.</param>
/// <param name="Extra">Optional structured extras the line carried beyond the
/// core stats (e.g. "remaining=4041" on a ward absorb) — kept so future app
/// features can derive richer views without re-parsing logs.</param>
/// <param name="ObservedAt">Wall-clock arrival stamp (live tailing only; null
/// on imports). Sub-second rich data for replay spacing and cross-log
/// alignment — NEVER used in ACT/site-compatible stat math, which stays on
/// the whole-second <paramref name="Time"/>.</param>
public sealed record Swing(
    SwingCategory Category,
    bool Critical,
    string Special,
    string Attacker,
    string Ability,
    DamageValue Damage,
    DateTimeOffset Time,
    int TimeSorter,
    string Victim,
    string DamageType,
    string? Extra = null,
    DateTimeOffset? ObservedAt = null)
{
    /// <summary>The pseudo-ability plain melee swings record under — the
    /// Auto-Attack vs skill split every stat surface keys on. Lives in
    /// Combat (the bottom layer); the grammar aliases it.</summary>
    public const string AutoAttackAbility = "Auto-Attack";

    /// <summary>The name heuristic ACT uses everywhere: a name containing a
    /// space is an NPC/pet, not a player.</summary>
    public static bool LooksLikePlayer(string name) =>
        name.Length > 0 && !name.Contains(' ') && name != Combatant.UnknownName;
}
