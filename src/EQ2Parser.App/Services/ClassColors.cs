using System.Windows.Media;

namespace EQ2Parser.App.Services;

/// <summary>Archetype colours per class — mirrors EQ2Lexicon's classes.db
/// (Fighter red, Priest green, Scout yellow, Mage blue).</summary>
public static class ClassColors
{
    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static readonly SolidColorBrush Fighter = Frozen("#F87171");
    public static readonly SolidColorBrush Priest = Frozen("#4ADE80");
    public static readonly SolidColorBrush Scout = Frozen("#FBBF24");
    public static readonly SolidColorBrush Mage = Frozen("#93B4FF");
    public static readonly SolidColorBrush Neutral = Frozen("#8B90AB");

    // Ability source tags (drill-down): own class kit / raid-granted / item proc.
    public static readonly SolidColorBrush SourceClass = Frozen("#C8A96E");
    public static readonly SolidColorBrush SourceRaid = Frozen("#93D9FF");
    public static readonly SolidColorBrush SourceItem = Frozen("#9D9D9D");

    // Parse-tree text: default, gold headers, and fight outcomes.
    public static readonly SolidColorBrush TreeText = Frozen("#E2E4F0");
    public static readonly SolidColorBrush TreeHeader = Frozen("#C8A96E");
    public static readonly SolidColorBrush OutcomeWin = Frozen("#4ADE80");
    public static readonly SolidColorBrush OutcomePartial = Frozen("#FBBF24");
    public static readonly SolidColorBrush OutcomeLoss = Frozen("#F87171");

    private static readonly Dictionary<string, SolidColorBrush> ByClass = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Guardian"] = Fighter, ["Berserker"] = Fighter, ["Paladin"] = Fighter,
        ["Shadowknight"] = Fighter, ["Monk"] = Fighter, ["Bruiser"] = Fighter,
        ["Templar"] = Priest, ["Inquisitor"] = Priest, ["Warden"] = Priest,
        ["Fury"] = Priest, ["Mystic"] = Priest, ["Defiler"] = Priest, ["Channeler"] = Priest,
        ["Ranger"] = Scout, ["Assassin"] = Scout, ["Swashbuckler"] = Scout, ["Brigand"] = Scout,
        ["Troubador"] = Scout, ["Dirge"] = Scout, ["Beastlord"] = Scout,
        ["Wizard"] = Mage, ["Warlock"] = Mage, ["Conjuror"] = Mage,
        ["Necromancer"] = Mage, ["Illusionist"] = Mage, ["Coercer"] = Mage,
    };

    public static SolidColorBrush For(string? className) =>
        className is not null && ByClass.TryGetValue(className, out var brush) ? brush : Neutral;
}
