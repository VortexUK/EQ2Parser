namespace EQ2Parser.Core.Triggers;

/// <summary>
/// The closed vocabularies behind timer tags. Neither is open text: EQ2
/// logs name exactly these damage schools, and control effects reduce to
/// the tooltip words players actually use. Single source of truth for the
/// miner, the curation workbench, and the timer editor dropdowns.
/// </summary>
public static class Vocabulary
{
    /// <summary>Damage schools as the log spells them ("crushing damage",
    /// "heat damage"). A mined tag may hold a comma-joined pair when an
    /// ability genuinely deals two ("poison, disease").</summary>
    public static readonly IReadOnlyList<string> DamageSchools =
    [
        "crushing", "slashing", "piercing",
        "heat", "cold", "magic", "mental", "divine",
        "disease", "poison", "focus",
    ];

    /// <summary>Control effects in tooltip vocabulary — the set worth a
    /// timer warning. Charm and knockup never log status lines but exist
    /// in-game, so they stay hand-taggable.</summary>
    public static readonly IReadOnlyList<string> ControlEffects =
        ["stun", "stifle", "mez", "fear", "root", "snare", "daze", "charm", "knockup"];

    /// <summary>Log status word → canonical control effect, or null for
    /// flavour-only adjectives (gloomy, seared, confused, unnerved…) whose
    /// real mechanic the log doesn't reveal — those stay untagged rather
    /// than guessed. The 2026-07 log sweep showed boss abilities land
    /// overwhelmingly in the canonical table; the flavour words are almost
    /// all raid-on-mob debuffs.</summary>
    /// <summary>Canonical effect → the word a mass-detriment callout speaks
    /// ("8 players stunned").</summary>
    public static string CalloutWord(string canonical) => canonical switch
    {
        "stun" => "stunned",
        "stifle" => "stifled",
        "mez" => "mesmerized",
        "fear" => "feared",
        "root" => "rooted",
        "snare" => "snared",
        "daze" => "dazed",
        "charm" => "charmed",
        "knockup" => "knocked up",
        _ => canonical,
    };

    public static string? CanonicalControlEffect(string? logWord) => logWord switch
    {
        "stunned" or "stupified" => "stun",
        "silenced" => "stifle",
        "mesmerized" or "dazzled" => "mez",
        "terrified" or "afraid" or "feared" => "fear",
        "frozen" or "rooted" => "root",
        "snared" => "snare",
        "dazed" => "daze",
        _ => null,
    };
}
