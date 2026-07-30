using System.Globalization;
using System.Text.RegularExpressions;
using EQ2Parser.Core.Combat;

namespace EQ2Parser.Core.Grammar;

/// <summary>
/// EQ2 English combat-line grammar (cleanroom — patterns derived from real
/// TLE-era logs, see docs/act-behavior.md for the compatibility rules).
///
/// Perspective: "YOU"/"YOUR" resolve to the log owner's name at the driver
/// level, not here — the grammar returns the literal "YOU" marker.
///
/// Melee vs non-melee follows the damage school: crushing/piercing/slashing
/// are melee (auto-attack family); everything else is a skill/spell.
/// </summary>
public static partial class EnglishGrammar
{
    /// <summary>Marker returned for first-person actors; the driver replaces
    /// it with the log owner's character name.</summary>
    public const string You = "YOU";

    public const string AutoAttackAbility = "Auto-Attack";

    private static readonly HashSet<string> MeleeSchools = new(StringComparer.OrdinalIgnoreCase)
    {
        "crushing", "piercing", "slashing",
    };

    // ── Damage ──────────────────────────────────────────────────────────────
    // YOUR Divine Strike hits a bog slug for a critical of 15,032 divine damage.
    // YOU hit a lesser shadowbone skeleton for a critical of 5,529 crushing damage.
    // Guard Moor hits a bog slug hatchling for 368 slashing damage.
    // Badbang's Magic Feedback hits Delhagin the Frightful for 14 magic damage.
    // Menludiir's unswerving hammer multi attacks a krait patriarch for a critical of 2,378 crushing damage.

    private const string DamageVerbs = "hits|hit|multi attacks|double attacks|flurries|aoe attacks";

    [GeneratedRegex($@"^YOUR (?<ability>.+?) (?<verb>{DamageVerbs}) (?<victim>.+?) for (?<crit>a critical of )?(?<amount>[\d,]+) (?<school>\w+) damage\.$")]
    private static partial Regex YourAbilityDamage();

    [GeneratedRegex($@"^YOU (?<verb>{DamageVerbs}) (?<victim>.+?) for (?<crit>a critical of )?(?<amount>[\d,]+) (?<school>\w+) damage\.$")]
    private static partial Regex YouAutoDamage();

    [GeneratedRegex($@"^(?<attacker>.+?)'s (?<ability>.+?) (?<verb>{DamageVerbs}) (?<victim>.+?) for (?<crit>a critical of )?(?<amount>[\d,]+) (?<school>\w+) damage\.$")]
    private static partial Regex PossessiveAbilityDamage();

    [GeneratedRegex($@"^(?<attacker>.+?) (?<verb>{DamageVerbs}) (?<victim>.+?) for (?<crit>a critical of )?(?<amount>[\d,]+) (?<school>\w+) damage\.$")]
    private static partial Regex PlainDamage();

    // ── Avoids ──────────────────────────────────────────────────────────────
    // a krait patriarch tries to pierce YOU, but misses.
    // a krait patriarch tries to crush Menludiir, but Menludiir parries.
    // ... but YOU block. / but YOU resist. / but Badbang ripostes. / but X dodges.

    [GeneratedRegex(@"^(?<attacker>.+?) tries to (?<verb>\w+) (?<victim>.+?), but (?:(?<outcomeactor>.+?) )?(?<outcome>misses|parries|ripostes|blocks|resists|dodges|block|resist|riposte|parry|dodge)\.$")]
    private static partial Regex AvoidLine();

    // ── Heals ───────────────────────────────────────────────────────────────
    // YOUR Reverence heals YOU for 13 hit points.
    // Sofja's Grim Sorcery heals Menludiir for a critical of 1,024 hit points.

    [GeneratedRegex(@"^YOUR (?<ability>.+?) heals (?<victim>.+?) for (?<crit>a critical of )?(?<amount>[\d,]+) hit points?\.$")]
    private static partial Regex YourHeal();

    [GeneratedRegex(@"^(?<attacker>.+?)'s (?<ability>.+?) heals (?<victim>.+?) for (?<crit>a critical of )?(?<amount>[\d,]+) hit points?\.$")]
    private static partial Regex PossessiveHeal();

    // ── Deaths ──────────────────────────────────────────────────────────────
    // You have killed a glacial tunneler.
    // Alas, a Thurgadin watcher has died from pain and suffering.
    // <victim> has been slain by <killer>!

    [GeneratedRegex(@"^You have killed (?<victim>.+?)\.$")]
    private static partial Regex YouKilled();

    [GeneratedRegex(@"^Alas, (?<victim>.+?) has died(?: from pain and suffering)?\.$")]
    private static partial Regex AlasDied();

    [GeneratedRegex(@"^(?<victim>.+?) has been slain by (?<killer>.+?)!$")]
    private static partial Regex SlainBy();

    // ── Zone ────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^You have entered (?<zone>.+?)\.$")]
    private static partial Regex ZoneEntered();

    /// <summary>Parse one log message. Null = not a line this grammar knows.</summary>
    public static GrammarEvent? TryParse(string message)
    {
        // Ordering matters: possessive forms must be tried before the plain
        // form ("Badbang's Magic Feedback hits …" must not parse with
        // attacker "Badbang's Magic Feedback").
        Match m;

        if ((m = YourAbilityDamage().Match(message)).Success)
            return Damage(m, You, m.Groups["ability"].Value);
        if ((m = YouAutoDamage().Match(message)).Success)
            return Damage(m, You, AutoAttackAbility);
        if ((m = PossessiveAbilityDamage().Match(message)).Success)
            return Damage(m, m.Groups["attacker"].Value, m.Groups["ability"].Value);
        if ((m = AvoidLine().Match(message)).Success)
            return Avoid(m);
        if ((m = PlainDamage().Match(message)).Success)
            return Damage(m, m.Groups["attacker"].Value, AutoAttackAbility);
        if ((m = YourHeal().Match(message)).Success)
            return Heal(m, You);
        if ((m = PossessiveHeal().Match(message)).Success)
            return Heal(m, m.Groups["attacker"].Value);
        if ((m = YouKilled().Match(message)).Success)
            return new DeathEvent(You, m.Groups["victim"].Value);
        if ((m = AlasDied().Match(message)).Success)
            return new DeathEvent("Unknown", m.Groups["victim"].Value);
        if ((m = SlainBy().Match(message)).Success)
            return new DeathEvent(m.Groups["killer"].Value, m.Groups["victim"].Value);
        if ((m = ZoneEntered().Match(message)).Success)
            return new ZoneEvent(m.Groups["zone"].Value);

        return null;
    }

    private static SwingEvent Damage(Match m, string attacker, string ability)
    {
        var school = m.Groups["school"].Value;
        var category = MeleeSchools.Contains(school) ? SwingCategory.Melee : SwingCategory.NonMelee;
        return new SwingEvent(
            category,
            Critical: m.Groups["crit"].Success,
            Special: SpecialFromVerb(m.Groups["verb"].Value),
            Attacker: attacker,
            Ability: ability,
            Damage: ParseAmount(m.Groups["amount"].Value),
            Victim: m.Groups["victim"].Value,
            DamageType: school);
    }

    private static SwingEvent Heal(Match m, string attacker) => new(
        SwingCategory.Healing,
        Critical: m.Groups["crit"].Success,
        Special: "None",
        Attacker: attacker,
        Ability: m.Groups["ability"].Value,
        Damage: ParseAmount(m.Groups["amount"].Value),
        Victim: m.Groups["victim"].Value,
        DamageType: "heal");

    private static SwingEvent Avoid(Match m)
    {
        var outcome = m.Groups["outcome"].Value;
        DamageValue damage = outcome switch
        {
            "misses" => DamageValue.Miss,
            "parries" or "parry" => new DamageValue(DamageValue.ParryNumber),
            "ripostes" or "riposte" => new DamageValue(DamageValue.RiposteNumber),
            "blocks" or "block" => new DamageValue(DamageValue.BlockNumber),
            "resists" or "resist" => new DamageValue(DamageValue.ResistNumber),
            _ => DamageValue.Unknown("Dodge"),
        };
        return new SwingEvent(
            SwingCategory.Melee,
            Critical: false,
            Special: "None",
            Attacker: m.Groups["attacker"].Value,
            Ability: AutoAttackAbility,
            Damage: damage,
            Victim: m.Groups["victim"].Value,
            DamageType: "avoided");
    }

    private static string SpecialFromVerb(string verb) => verb switch
    {
        "hits" or "hit" => "None",
        "multi attacks" => "Multi Attack",
        "double attacks" => "Double Attack",
        "flurries" => "Flurry",
        "aoe attacks" => "AoE Attack",
        _ => "None",
    };

    private static DamageValue ParseAmount(string text) =>
        new(long.Parse(text, NumberStyles.AllowThousands, CultureInfo.InvariantCulture));
}
