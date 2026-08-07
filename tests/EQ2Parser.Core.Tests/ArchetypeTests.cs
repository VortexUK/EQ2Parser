using EQ2Parser.Core.Analysis;

namespace EQ2Parser.Core.Tests;

public class ArchetypeTests
{
    [Theory]
    [InlineData("Guardian", Archetype.Fighter)]
    [InlineData("Bruiser", Archetype.Fighter)]
    [InlineData("Templar", Archetype.Priest)]
    [InlineData("Channeler", Archetype.Priest)]
    [InlineData("Dirge", Archetype.Scout)]
    [InlineData("Beastlord", Archetype.Scout)]
    [InlineData("Wizard", Archetype.Mage)]
    [InlineData("Conjuror", Archetype.Mage)]
    [InlineData("templar", Archetype.Priest)] // case-insensitive
    public void Maps_Every_Class(string cls, string expected) =>
        Assert.Equal(expected, Archetype.Of(cls));

    [Fact]
    public void Unknown_Or_Null_Is_Null()
    {
        Assert.Null(Archetype.Of(null));
        Assert.Null(Archetype.Of(""));
        Assert.Null(Archetype.Of("Avatar of Fright"));
    }

    [Fact]
    public void All_TwentySix_Subclasses_Are_Mapped()
    {
        string[] classes =
        [
            "Guardian", "Berserker", "Paladin", "Shadowknight", "Monk", "Bruiser",
            "Templar", "Inquisitor", "Warden", "Fury", "Mystic", "Defiler", "Channeler",
            "Ranger", "Assassin", "Swashbuckler", "Brigand", "Dirge", "Troubador", "Beastlord",
            "Wizard", "Warlock", "Illusionist", "Coercer", "Necromancer", "Conjuror",
        ];
        foreach (var cls in classes)
            Assert.NotNull(Archetype.Of(cls));
    }
}
