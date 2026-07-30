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
