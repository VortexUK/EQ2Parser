using EQ2Parser.Core.Analysis;

namespace EQ2Parser.Core.Tests;

public class RaidBuffAttributorTests
{
    // Blade Chime is the canonical case: an effects-layer name granted by
    // Dirges, logged under every beneficiary.
    private static readonly SpellClassMap Map = SpellClassMap.FromDictionary(
        spells: new(),
        effects: new()
        {
            ["blade chime"] = ["Dirge"],
            ["precise note"] = ["Troubador"],
            ["shared boon"] = ["Dirge", "Troubador"],
        });

    // Real-world shape: the BENEFICIARIES (Eskel the Swashbuckler et al.)
    // are whose swings carry the proc; the attributor's job is crediting
    // the DIRGE who granted it. Beneficiaries never appear as granters.
    private static readonly Dictionary<string, string> Allies = new()
    {
        ["Ariadneh"] = "Dirge",
        ["Eskel"] = "Swashbuckler",
        ["Menludiir"] = "Templar",
        ["Waverat"] = "Troubador",
    };

    [Fact]
    public void Single_Granter_Gets_Full_Exact_Credit()
    {
        var credits = new RaidBuffAttributor(Map).Attribute([("Blade Chime", 1000)], Allies);
        var credit = Assert.Single(credits);
        Assert.Equal(["Ariadneh"], credit.Granters);
        Assert.False(credit.Estimated);
        Assert.Equal(1000, RaidBuffAttributor.CreditByGranter(credits)["Ariadneh"]);
    }

    [Fact]
    public void Two_Granters_Split_Evenly_And_Are_Flagged_Estimated()
    {
        var twoDirges = new Dictionary<string, string>(Allies) { ["Fiix"] = "Dirge" };
        var credits = new RaidBuffAttributor(Map).Attribute([("Blade Chime", 1000)], twoDirges);
        var credit = Assert.Single(credits);
        Assert.True(credit.Estimated);
        Assert.Equal(2, credit.Granters.Count);
        var byGranter = RaidBuffAttributor.CreditByGranter(credits);
        Assert.Equal(500, byGranter["Ariadneh"]);
        Assert.Equal(500, byGranter["Fiix"]);
    }

    [Fact]
    public void No_Granting_Class_Present_Is_Unattributed_Not_Guessed()
    {
        var noTroub = new Dictionary<string, string> { ["Ariadneh"] = "Dirge" };
        var credits = new RaidBuffAttributor(Map).Attribute([("Precise Note", 800)], noTroub);
        var credit = Assert.Single(credits);
        Assert.Empty(credit.Granters);
        Assert.Empty(RaidBuffAttributor.CreditByGranter(credits));
    }

    [Fact]
    public void MultiClass_Grant_Splits_Across_Both_Classes()
    {
        var credits = new RaidBuffAttributor(Map).Attribute([("Shared Boon", 1000)], Allies);
        var credit = Assert.Single(credits);
        Assert.True(credit.Estimated);
        Assert.Equal(["Ariadneh", "Waverat"], credit.Granters);
        var byGranter = RaidBuffAttributor.CreditByGranter(credits);
        Assert.Equal(500, byGranter["Ariadneh"]);
        Assert.Equal(500, byGranter["Waverat"]);
    }

    [Fact]
    public void Roman_Numeral_Tiers_Resolve_Through_Normalization()
    {
        var credits = new RaidBuffAttributor(Map).Attribute([("Blade Chime IV", 100)], Allies);
        Assert.Equal(["Ariadneh"], Assert.Single(credits).Granters);
    }

    [Fact]
    public void Unmapped_Ability_Is_Unattributed()
    {
        // An override can force Raid for a name the map has never seen —
        // the attribution must degrade to unattributed, not throw.
        var credits = new RaidBuffAttributor(Map).Attribute([("Mystery Proc", 100)], Allies);
        Assert.Empty(Assert.Single(credits).Granters);
    }
}
