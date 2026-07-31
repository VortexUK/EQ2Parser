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
/// Melee (auto-attack) vs non-melee (skill) follows the ABILITY, never the
/// damage school — infusions can make autoattacks deal any school, and
/// combat arts routinely deal crushing/piercing/slashing.
/// </summary>
public static partial class EnglishGrammar
{
    /// <summary>Marker returned for first-person actors; the driver replaces
    /// it with the log owner's character name.</summary>
    public const string You = "YOU";

    public const string AutoAttackAbility = "Auto-Attack";

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

    [GeneratedRegex($@"^(?<attacker>.+?)'s? (?<ability>.+?) (?<verb>{DamageVerbs}) (?<victim>.+?) for (?<crit>a critical of )?(?<amount>[\d,]+) (?<school>\w+) damage\.$")]
    private static partial Regex PossessiveAbilityDamage();

    [GeneratedRegex($@"^(?<attacker>.+?) (?<verb>{DamageVerbs}) (?<victim>.+?) for (?<crit>a critical of )?(?<amount>[\d,]+) (?<school>\w+) damage\.$")]
    private static partial Regex PlainDamage();

    // a creepfern is hit by Acid Spray for 447 poison damage.  (anonymous
    // source — dumbfires/ground effects; attributed to "Unknown". Must be
    // tried before PlainDamage, which would split this into a phantom
    // attacker "a creepfern is" and victim "by Acid Spray".)
    [GeneratedRegex(@"^(?<victim>.+?) is hit by (?<ability>.+?) for (?<crit>a critical of )?(?<amount>[\d,]+) (?<school>\w+) damage\.$")]
    private static partial Regex AnonymousHit();

    // ── Avoids ──────────────────────────────────────────────────────────────
    // a krait patriarch tries to pierce YOU, but misses.
    // a krait patriarch tries to crush Menludiir, but Menludiir parries.
    // ... but YOU block. / but YOU resist. / but Badbang ripostes. / but X dodges.

    [GeneratedRegex(@"^(?<attacker>.+?) tries to (?<verb>\w+) (?<victim>.+?), but (?:(?<outcomeactor>.+?) )?(?<outcome>misses|parries|ripostes|blocks|resists|dodges|counters|block|resist|riposte|parry|dodge|counter)\.$")]
    private static partial Regex AvoidLine();

    // ── Heals ───────────────────────────────────────────────────────────────
    // YOUR Reverence heals YOU for 13 hit points.
    // Sofja's Grim Sorcery heals Menludiir for a critical of 1,024 hit points.

    [GeneratedRegex(@"^YOUR (?<ability>.+?) heals (?<victim>.+?) for (?<crit>a critical of )?(?<amount>[\d,]+) hit points?\.$")]
    private static partial Regex YourHeal();

    [GeneratedRegex(@"^(?<attacker>.+?)'s? (?<ability>.+?) heals (?<victim>.+?) for (?<crit>a critical of )?(?<amount>[\d,]+) hit points?\.$")]
    private static partial Regex PossessiveHeal();

    // ── Zero-damage hits ────────────────────────────────────────────────────
    // Shyoh's Vampiric Requiem hits Shyoh but fails to inflict any damage.
    // YOUR Smite hits a blood colossus but fails to inflict any damage.

    [GeneratedRegex(@"^YOUR (?<ability>.+?) hits (?<victim>.+?) but fails to inflict any damage\.$")]
    private static partial Regex YourNoDamage();

    [GeneratedRegex(@"^(?<attacker>.+?)'s? (?<ability>.+?) hits (?<victim>.+?) but fails to inflict any damage\.$")]
    private static partial Regex PossessiveNoDamage();

    [GeneratedRegex(@"^(?<attacker>.+?) hits (?<victim>.+?) but fails to inflict any damage\.$")]
    private static partial Regex PlainNoDamage();

    // ── First-person avoids ─────────────────────────────────────────────────
    // YOU try to pierce Malkonis D'Morte but miss.

    [GeneratedRegex(@"^YOU try to (?<verb>\w+) (?<victim>.+?),? but (?:(?<outcomeactor>.+?) )?(?<outcome>miss|misses|parries|ripostes|blocks|resists|dodges)\.$")]
    private static partial Regex YouAvoid();

    // ── Wards ───────────────────────────────────────────────────────────────
    // Alexsian's Abhorrent Shroud absorbs 443 points of damage from being done to Mempayc. (4041 points remaining)
    // ACT convention: a ward absorb is a HEAL with damage type "Ward (Absorb)".

    public const string WardAbsorbType = "Ward (Absorb)";

    [GeneratedRegex(@"^(?<attacker>.+?)'s? (?<ability>.+?) absorbs (?<amount>[\d,]+) points? of damage from being done to (?<victim>.+?)\.(?: \((?<remaining>[\d,]+) points? remaining\))?$")]
    private static partial Regex WardAbsorb();

    // YOUR Stonewill absorbs 720 points of damage from being done to YOURSELF. (479 points remaining)
    [GeneratedRegex(@"^YOUR (?<ability>.+?) absorbs (?<amount>[\d,]+) points? of damage from being done to (?<victim>.+?)\.(?: \((?<remaining>[\d,]+) points? remaining\))?$")]
    private static partial Regex YourWardAbsorb();

    // ── Power ───────────────────────────────────────────────────────────────
    // Tsuna's Empower Servant refreshes Tsuna for 59 mana points.

    [GeneratedRegex(@"^(?<attacker>.+?)'s? (?<ability>.+?) refreshes (?<victim>.+?) for (?<amount>[\d,]+) (?:mana|power) points?\.$")]
    private static partial Regex PowerRefresh();

    [GeneratedRegex(@"^YOUR (?<ability>.+?) refreshes (?<victim>.+?) for (?<amount>[\d,]+) (?:mana|power) points?\.$")]
    private static partial Regex YourPowerRefresh();

    // ── Threat ──────────────────────────────────────────────────────────────
    // Badbang's Insolent Gibe increases THEIR hate with a dragonspawn whelp for 1,234 threat.
    // Noxyi's Dynamism reduces THEIR hate with a blood colossus for 567 threat.

    [GeneratedRegex(@"^(?<attacker>.+?)'s? (?<ability>.+?) (?<direction>increases|reduces) (?:THEIR|YOUR) hate with (?<victim>.+?) for (?<amount>[\d,]+) threat\.$")]
    private static partial Regex ThreatChange();

    [GeneratedRegex(@"^YOUR (?<ability>.+?) (?<direction>increases|reduces) YOUR hate with (?<victim>.+?) for (?<amount>[\d,]+) threat\.$")]
    private static partial Regex YourThreatChange();

    // ── Cures ───────────────────────────────────────────────────────────────
    // Catofur's Cure Trauma relieves Eviscerate from Badbang.

    [GeneratedRegex(@"^(?<attacker>.+?)'s? (?<ability>.+?) relieves (?<effect>.+?) from (?<victim>.+?)\.$")]
    private static partial Regex CureRelieves();

    [GeneratedRegex(@"^YOUR (?<ability>.+?) relieves (?<effect>.+?) from (?<victim>.+?)\.$")]
    private static partial Regex YourCureRelieves();

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

    // Mayong Mistmoore dies.  (the generic third-person death — how raid
    // members witness a boss kill; no killer attribution on the line)
    [GeneratedRegex(@"^(?<victim>.+?) dies\.$")]
    private static partial Regex Dies();

    // Wuoshi has killed Lozzy.  (third-person kill attribution)
    [GeneratedRegex(@"^(?<killer>.+?) has killed (?<victim>.+?)\.$")]
    private static partial Regex HasKilled();

    // Wuoshi's has been banished!  (epic-mob death flavor; the stray
    // possessive is a game typo we tolerate)
    [GeneratedRegex(@"^(?<victim>.+?)(?:'s)? has been banished!$")]
    private static partial Regex Banished();

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
        if ((m = AnonymousHit().Match(message)).Success)
            return Damage(m, "Unknown", m.Groups["ability"].Value);
        if ((m = PlainDamage().Match(message)).Success)
            return Damage(m, m.Groups["attacker"].Value, AutoAttackAbility);
        if ((m = YourHeal().Match(message)).Success)
            return Heal(m, You);
        if ((m = PossessiveHeal().Match(message)).Success)
            return Heal(m, m.Groups["attacker"].Value);
        if ((m = YouAvoid().Match(message)).Success)
            return AvoidFrom(m, You);
        if ((m = YourNoDamage().Match(message)).Success)
            return NoDamageHit(m, You, m.Groups["ability"].Value);
        if ((m = PossessiveNoDamage().Match(message)).Success)
            return NoDamageHit(m, m.Groups["attacker"].Value, m.Groups["ability"].Value);
        if ((m = PlainNoDamage().Match(message)).Success)
            return NoDamageHit(m, m.Groups["attacker"].Value, AutoAttackAbility);
        if ((m = YourWardAbsorb().Match(message)).Success)
            return Ward(m, You);
        if ((m = WardAbsorb().Match(message)).Success)
            return Ward(m, m.Groups["attacker"].Value);
        if ((m = YourPowerRefresh().Match(message)).Success)
            return PowerReplenish(m, You);
        if ((m = PowerRefresh().Match(message)).Success)
            return PowerReplenish(m, m.Groups["attacker"].Value);
        if ((m = YourThreatChange().Match(message)).Success)
            return Threat(m, You);
        if ((m = ThreatChange().Match(message)).Success)
            return Threat(m, m.Groups["attacker"].Value);
        if ((m = YourCureRelieves().Match(message)).Success)
            return Cure(m, You);
        if ((m = CureRelieves().Match(message)).Success)
            return Cure(m, m.Groups["attacker"].Value);
        if ((m = YouKilled().Match(message)).Success)
            return new DeathEvent(You, m.Groups["victim"].Value);
        if ((m = AlasDied().Match(message)).Success)
            return new DeathEvent("Unknown", m.Groups["victim"].Value);
        if ((m = SlainBy().Match(message)).Success)
            return new DeathEvent(m.Groups["killer"].Value, m.Groups["victim"].Value);
        if ((m = HasKilled().Match(message)).Success)
            return new DeathEvent(m.Groups["killer"].Value, m.Groups["victim"].Value);
        // "Rusk's fae fire sputters and dies." — a dumbfire expiring
        // naturally, not a combat death; must not feed the Dies() shape.
        if (message.EndsWith(" sputters and dies.", StringComparison.Ordinal))
            return null;
        if ((m = Dies().Match(message)).Success)
            return new DeathEvent("Unknown", m.Groups["victim"].Value);
        if ((m = Banished().Match(message)).Success)
            return new DeathEvent("Unknown", m.Groups["victim"].Value);
        if ((m = ZoneEntered().Match(message)).Success)
            return new ZoneEvent(m.Groups["zone"].Value);

        return null;
    }

    private static SwingEvent Damage(Match m, string attacker, string ability)
    {
        var school = m.Groups["school"].Value;
        // Auto-attack vs skill follows the ABILITY alone, never the school:
        // named abilities are skills even when they deal
        // crushing/piercing/slashing (an Assassin's whole kit), and plain
        // attacks plus the multi/double/flurry/AoE autoattack verbs are
        // melee even when infused to another school entirely (Asame's
        // autoattack deals disease). Matches ACT's Auto-Attack (Out) /
        // Skill/Ability (Out) split.
        var special = SpecialFromVerb(m.Groups["verb"].Value);
        var category = ability == AutoAttackAbility || special != "None"
            ? SwingCategory.Melee
            : SwingCategory.NonMelee;
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

    private static SwingEvent NoDamageHit(Match m, string attacker, string ability) => new(
        ability == AutoAttackAbility ? SwingCategory.Melee : SwingCategory.NonMelee,
        Critical: false,
        Special: "None",
        Attacker: attacker,
        Ability: ability,
        Damage: DamageValue.NoDamage,
        Victim: m.Groups["victim"].Value,
        DamageType: "none");

    private static SwingEvent PowerReplenish(Match m, string attacker) => new(
        SwingCategory.PowerReplenish,
        Critical: false,
        Special: "None",
        Attacker: attacker,
        Ability: m.Groups["ability"].Value,
        Damage: ParseAmount(m.Groups["amount"].Value),
        Victim: m.Groups["victim"].Value,
        DamageType: "power");

    private static SwingEvent Threat(Match m, string attacker) => new(
        SwingCategory.Threat,
        Critical: false,
        Special: "None",
        Attacker: attacker,
        Ability: m.Groups["ability"].Value,
        Damage: ParseAmount(m.Groups["amount"].Value),
        Victim: m.Groups["victim"].Value,
        DamageType: m.Groups["direction"].Value == "reduces" ? "threat-reduce" : "threat");

    private static SwingEvent Cure(Match m, string attacker) => new(
        SwingCategory.CureDispel,
        Critical: false,
        Special: "None",
        Attacker: attacker,
        Ability: m.Groups["ability"].Value,
        Damage: DamageValue.NoDamage,
        Victim: m.Groups["victim"].Value,
        DamageType: m.Groups["effect"].Value);

    private static SwingEvent Ward(Match m, string attacker) => new(
        SwingCategory.Healing, false, "None",
        attacker, m.Groups["ability"].Value,
        ParseAmount(m.Groups["amount"].Value), m.Groups["victim"].Value, WardAbsorbType,
        Extra: m.Groups["remaining"].Success ? $"remaining={m.Groups["remaining"].Value.Replace(",", "")}" : null);

    private static SwingEvent Avoid(Match m) => AvoidFrom(m, m.Groups["attacker"].Value);

    private static SwingEvent AvoidFrom(Match m, string attacker)
    {
        var outcome = m.Groups["outcome"].Value;
        DamageValue damage = outcome switch
        {
            "misses" or "miss" => DamageValue.Miss,
            "parries" or "parry" => new DamageValue(DamageValue.ParryNumber),
            "ripostes" or "riposte" => new DamageValue(DamageValue.RiposteNumber),
            "blocks" or "block" => new DamageValue(DamageValue.BlockNumber),
            "resists" or "resist" => new DamageValue(DamageValue.ResistNumber),
            "counters" or "counter" => DamageValue.Unknown("Counter"),
            _ => DamageValue.Unknown("Dodge"),
        };

        // "tries to slash Mempayc with Barrage, but Mempayc parries." — a
        // melee special: split the ability off the victim. The outcome actor
        // is the true victim name when present, so prefer that cross-check
        // (it keeps abilities that themselves contain " with " intact);
        // otherwise split at the first " with " ("but misses" carries no
        // actor, and weapon-avoids name the weapon rather than the victim).
        var victim = m.Groups["victim"].Value;
        var ability = AutoAttackAbility;
        var actor = m.Groups["outcomeactor"].Value;
        if (actor.Length > 0 && victim.StartsWith(actor + " with ", StringComparison.Ordinal))
        {
            ability = victim[(actor.Length + " with ".Length)..];
            victim = actor;
        }
        else if (victim.IndexOf(" with ", StringComparison.Ordinal) is var idx and > 0)
        {
            ability = victim[(idx + " with ".Length)..];
            victim = victim[..idx];
        }

        // Preserve WHO defeated the attack when it wasn't the victim
        // themselves — another player covering them, or a parrying weapon
        // ("but Ahuli blocks." / "but Menludiir's unswerving hammer
        // parries."). Feeds the avoidance report's helper attribution.
        string? extra = null;
        if (actor.Length > 0 && !actor.Equals(victim, StringComparison.OrdinalIgnoreCase))
            extra = "by=" + actor;

        return new SwingEvent(
            ability == AutoAttackAbility ? SwingCategory.Melee : SwingCategory.NonMelee,
            Critical: false,
            Special: "None",
            Attacker: attacker,
            Ability: ability,
            Damage: damage,
            Victim: victim,
            DamageType: "avoided",
            Extra: extra);
    }

    private static string SpecialFromVerb(string verb) => verb switch
    {
        "hits" or "hit" => "None",
        "multi attacks" => "Multi Attack",
        "double attacks" => "Multi Attack", // era rename - same mechanic
        "flurries" => "Flurry",
        "aoe attacks" => "AoE Attack",
        _ => "None",
    };

    private static DamageValue ParseAmount(string text) =>
        new(long.Parse(text, NumberStyles.AllowThousands, CultureInfo.InvariantCulture));
}
