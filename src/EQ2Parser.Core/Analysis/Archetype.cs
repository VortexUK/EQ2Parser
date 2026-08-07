namespace EQ2Parser.Core.Analysis;

/// <summary>EQ2's four class archetypes — the grouping the export filter
/// offers ("only healers", "scouts + mages").</summary>
public static class Archetype
{
    public const string Fighter = "Fighter";
    public const string Priest = "Priest";
    public const string Scout = "Scout";
    public const string Mage = "Mage";

    public static readonly IReadOnlyList<string> All = [Fighter, Priest, Scout, Mage];

    private static readonly Dictionary<string, string> ByClass = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Guardian"] = Fighter, ["Berserker"] = Fighter, ["Paladin"] = Fighter,
        ["Shadowknight"] = Fighter, ["Monk"] = Fighter, ["Bruiser"] = Fighter,
        ["Templar"] = Priest, ["Inquisitor"] = Priest, ["Warden"] = Priest,
        ["Fury"] = Priest, ["Mystic"] = Priest, ["Defiler"] = Priest, ["Channeler"] = Priest,
        ["Ranger"] = Scout, ["Assassin"] = Scout, ["Swashbuckler"] = Scout, ["Brigand"] = Scout,
        ["Dirge"] = Scout, ["Troubador"] = Scout, ["Beastlord"] = Scout,
        ["Wizard"] = Mage, ["Warlock"] = Mage, ["Illusionist"] = Mage, ["Coercer"] = Mage,
        ["Necromancer"] = Mage, ["Conjuror"] = Mage,
    };

    /// <summary>The archetype for a detected class name, or null when the
    /// class is unknown/undetected (callers decide how to filter those).</summary>
    public static string? Of(string? className) =>
        className is not null && ByClass.TryGetValue(className, out var a) ? a : null;
}
