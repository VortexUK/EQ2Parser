using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// Impossible-weapon renamed-pet detection: the class → auto-attack-school
/// rules plus the per-school tally on Combatant that feeds them.
/// </summary>
public class MeleeCapabilitiesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    // Clerics: crushing only — both other schools impossible.
    [InlineData("Templar", "piercing", true)]
    [InlineData("Templar", "slashing", true)]
    [InlineData("Templar", "crushing", false)]
    [InlineData("Inquisitor", "piercing", true)]
    // Shamans: spears make piercing legitimate; slashing stays impossible.
    [InlineData("Mystic", "piercing", false)]
    [InlineData("Mystic", "slashing", true)]
    [InlineData("Defiler", "slashing", true)]
    // Mages: daggers make piercing legitimate; slashing stays impossible.
    [InlineData("Necromancer", "piercing", false)]
    [InlineData("Necromancer", "slashing", true)]
    [InlineData("Wizard", "slashing", true)]
    // Unrestricted archetypes: never flagged.
    [InlineData("Guardian", "piercing", false)]
    [InlineData("Assassin", "slashing", false)]
    [InlineData("Fury", "slashing", false)]
    // Infused auto-attack schools never match the physical three.
    [InlineData("Templar", "disease", false)]
    [InlineData("Templar", "heat", false)]
    public void Impossible_School_Rules(string cls, string school, bool impossible) =>
        Assert.Equal(impossible, MeleeCapabilities.IsImpossibleSchool(cls, school));

    [Fact]
    public void Combatant_Tallies_AutoAttack_By_School()
    {
        var engine = new ParserEngine("log", "Menlu");
        Assert.True(engine.SetEncounter(T0, "Menlu", "a gnoll"));
        // The Templar's own hammer swings...
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menlu", Grammar.EnglishGrammar.AutoAttackAbility, 100, T0, "a gnoll", "crushing");
        engine.AddSwing(SwingCategory.Melee, true, "None", "Menlu", Grammar.EnglishGrammar.AutoAttackAbility, 300, T0, "a gnoll", "crushing");
        // ...a renamed pet's piercing stream merged under the same name...
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menlu", Grammar.EnglishGrammar.AutoAttackAbility, 500, T0, "a gnoll", "piercing");
        // ...a miss (sentinel damage) that must not count as a hit...
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menlu", Grammar.EnglishGrammar.AutoAttackAbility, DamageValue.MissNumber, T0, "a gnoll", "piercing");
        // ...and a named skill that must not enter the auto-attack tally.
        engine.AddSwing(SwingCategory.NonMelee, false, "None", "Menlu", "Smite", 250, T0, "a gnoll", "divine");
        engine.EndCombat();

        var combatant = engine.History[^1].Combatants["MENLU"];
        var bySchool = combatant.AutoAttackBySchool;

        Assert.Equal((400, 2), bySchool["crushing"]);
        Assert.Equal((500, 1), bySchool["piercing"]);
        Assert.False(bySchool.ContainsKey("divine"));
    }
}
