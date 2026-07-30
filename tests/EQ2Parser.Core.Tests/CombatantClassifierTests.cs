using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;

namespace EQ2Parser.Core.Tests;

public class CombatantClassifierTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    private static readonly SpellClassMap Fixture = SpellClassMap.FromDictionary(new()
    {
        ["divine strike"] = ["Templar"],
        ["reverence"] = ["Templar"],
        ["quick strike"] = ["Swashbuckler"],
    });

    [Theory]
    // Site KNOWN_EXAMPLES + regex-shaped auto-pet names.
    [InlineData("Gibab", true)]
    [InlineData("Zosn", true)]
    [InlineData("Kebn", true)]
    [InlineData("Zebekn", true)]
    [InlineData("Jentik", true)]
    [InlineData("jener", true)] // je + ner
    // Multi-word / empty / Unknown.
    [InlineData("Broomm's attack hawk", true)]
    [InlineData("Unknown", true)]
    [InlineData("", true)]
    // Real player names must survive.
    [InlineData("Menludiir", false)]
    [InlineData("Ariadneh", false)]
    [InlineData("Veyn", false)]
    [InlineData("Zeven", false)]
    [InlineData("Sausage", false)]
    public void Pet_Name_Shapes(string name, bool isPet) =>
        Assert.Equal(isPet, CombatantClassifier.IsPetName(name));

    [Fact]
    public void Classifies_Players_Pets_And_Enemies()
    {
        var engine = new ParserEngine("log", "Menlu");
        Assert.True(engine.SetEncounter(T0, "Menlu", "a gnoll"));
        engine.AddSwing(SwingCategory.NonMelee, false, "None", "Menlu", "Divine Strike", 100, T0, "a gnoll", "divine");
        engine.AddSwing(SwingCategory.Healing, false, "None", "Menlu", "Reverence", 50, T0, "Menlu", "heal");
        engine.AddSwing(SwingCategory.NonMelee, false, "None", "Bosun", "Quick Strike", 90, T0, "a gnoll", "piercing");
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menlu's warder", Grammar.EnglishGrammar.AutoAttackAbility, 40, T0, "a gnoll", "slashing");
        engine.AddSwing(SwingCategory.Melee, false, "None", "Gibab", Grammar.EnglishGrammar.AutoAttackAbility, 30, T0, "a gnoll", "crushing");
        engine.AddSwing(SwingCategory.Melee, false, "None", "a gnoll", Grammar.EnglishGrammar.AutoAttackAbility, 25, T0, "Menlu", "crushing");
        engine.AddSwing(SwingCategory.Melee, false, "None", "a gnoll's whelp", Grammar.EnglishGrammar.AutoAttackAbility, 15, T0, "Menlu", "piercing");
        // An out-of-group death witnessed mid-fight ("Alas, Crit has died…"):
        // the Unknown killer carries no ally polarity, so Crit never joins
        // the interaction graph.
        engine.AddSwing(SwingCategory.Melee, false, "None", "Unknown", Combatant.KillingAbility, DamageValue.Death, T0, "Crit", "death");
        engine.EndCombat();

        var tags = new CombatantClassifier(new ClassIdentifier(Fixture)).Classify(engine.History[^1]);

        Assert.Equal(CombatantKind.Player, tags["MENLU"].Kind);
        Assert.Equal("Templar", tags["MENLU"].Class.ClassName);
        Assert.Equal(CombatantKind.Player, tags["BOSUN"].Kind);

        Assert.Equal(CombatantKind.Pet, tags["MENLU'S WARDER"].Kind);
        Assert.Equal("Menlu", tags["MENLU'S WARDER"].PetOwner);
        Assert.Equal(CombatantKind.Pet, tags["GIBAB"].Kind);
        Assert.Null(tags["GIBAB"].PetOwner); // auto-named — no owner in the name

        Assert.Equal(CombatantKind.Enemy, tags["A GNOLL"].Kind);
        // Enemy possessives resolve their owner too, so the UI can nest them.
        Assert.Equal(CombatantKind.Enemy, tags["A GNOLL'S WHELP"].Kind);
        Assert.Equal("a gnoll", tags["A GNOLL'S WHELP"].PetOwner);
        Assert.Equal(CombatantKind.Bystander, tags["CRIT"].Kind);
        // The anonymous-source pseudo-combatant is bookkeeping, not a participant.
        Assert.Equal(CombatantKind.System, tags["UNKNOWN"].Kind);
    }

    [Fact]
    public void Small_Group_Unconfirmed_Stays_Pet_And_Raid_Fill_Promotes()
    {
        // ≤6 allies: no bucket-fill — the autoattack-only straggler is
        // unresolved ⇒ pet, matching the site pipeline.
        var small = new ParserEngine("log", "Menlu");
        Assert.True(small.SetEncounter(T0, "Menlu", "a gnoll"));
        small.AddSwing(SwingCategory.NonMelee, false, "None", "Menlu", "Divine Strike", 100, T0, "a gnoll", "divine");
        small.AddSwing(SwingCategory.Melee, false, "None", "Afklad", Grammar.EnglishGrammar.AutoAttackAbility, 10, T0, "a gnoll", "crushing");
        small.EndCombat();
        var classifier = new CombatantClassifier(new ClassIdentifier(Fixture));
        Assert.Equal(CombatantKind.Pet, classifier.Classify(small.History[^1])["AFKLAD"].Kind);

        // ≥11 allies: raid rules — highest-contributing unconfirmed promoted
        // toward the 24 cap.
        var raid = new ParserEngine("log", "Menlu");
        Assert.True(raid.SetEncounter(T0, "Menlu", "a dragon"));
        raid.AddSwing(SwingCategory.NonMelee, false, "None", "Menlu", "Divine Strike", 100, T0, "a dragon", "divine");
        for (var i = 0; i < 10; i++)
            raid.AddSwing(SwingCategory.NonMelee, false, "None", $"Player{i:D2}", "Quick Strike", 90, T0, "a dragon", "piercing");
        raid.AddSwing(SwingCategory.Melee, false, "None", "Afklad", Grammar.EnglishGrammar.AutoAttackAbility, 10, T0, "a dragon", "crushing");
        raid.EndCombat();
        Assert.Equal(CombatantKind.Player, classifier.Classify(raid.History[^1])["AFKLAD"].Kind);
    }
}
