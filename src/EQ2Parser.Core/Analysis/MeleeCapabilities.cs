namespace EQ2Parser.Core.Analysis;

/// <summary>
/// Which auto-attack damage schools each class can physically produce —
/// the impossible-weapon side of renamed-pet detection. A "piercing"
/// auto-attack stream under a detected Templar cannot be the Templar:
/// clerics can only equip crushing weapons, so those swings belong to a
/// pet another player renamed to this player's name.
///
/// Deliberately conservative: only relationships that are certain in the
/// EQ2 weapon rules are encoded (an alarm here accuses a player of
/// padding). Clerics use hammers/staves/great hammers only — both other
/// schools are impossible. Shamans add spears (piercing) and mages add
/// daggers (piercing), so only slashing is impossible for them. Druids,
/// fighters, and scouts are left unrestricted — fighters/scouts
/// legitimately span all three, and druid slashing access is uncertain.
/// Infused auto-attack (a school like disease/heat) never matches the
/// three physical schools, so it can't trip this.
/// </summary>
public static class MeleeCapabilities
{
    private const string Piercing = "piercing";
    private const string Slashing = "slashing";

    private static readonly HashSet<string> CrushOnlyClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Templar",
        "Inquisitor",
    };

    private static readonly HashSet<string> NoSlashClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mystic",
        "Defiler",
        "Wizard",
        "Warlock",
        "Illusionist",
        "Coercer",
        "Necromancer",
        "Conjuror",
    };

    /// <summary>True when <paramref name="school"/> is an auto-attack
    /// damage school the class cannot produce with any equippable weapon.</summary>
    public static bool IsImpossibleSchool(string detectedClass, string school)
    {
        if (string.Equals(school, Slashing, StringComparison.OrdinalIgnoreCase))
            return CrushOnlyClasses.Contains(detectedClass) || NoSlashClasses.Contains(detectedClass);
        if (string.Equals(school, Piercing, StringComparison.OrdinalIgnoreCase))
            return CrushOnlyClasses.Contains(detectedClass);
        return false;
    }
}
