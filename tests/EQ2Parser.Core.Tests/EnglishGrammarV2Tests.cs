using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Grammar;

namespace EQ2Parser.Core.Tests;

/// <summary>Coverage-pass shapes mined from a 115 MB raid log (all literals real).</summary>
public class EnglishGrammarV2Tests
{
    private static SwingEvent Swing(string message)
    {
        var parsed = EnglishGrammar.TryParse(message);
        return Assert.IsType<SwingEvent>(parsed);
    }

    [Fact]
    public void Ward_Absorb_Is_A_Heal_With_Ward_Type_And_Remaining_Extra()
    {
        var s = Swing("Alexsian's Abhorrent Shroud absorbs 443 points of damage from being done to Mempayc. (4041 points remaining)");
        Assert.Equal(SwingCategory.Healing, s.Category);
        Assert.Equal(("Alexsian", "Abhorrent Shroud", "Mempayc"), (s.Attacker, s.Ability, s.Victim));
        Assert.Equal(443, s.Damage.Number);
        Assert.Equal(EnglishGrammar.WardAbsorbType, s.DamageType);
        Assert.Equal("remaining=4041", s.Extra);
    }

    [Fact]
    public void Ward_Absorb_Without_Remaining_Suffix()
    {
        var s = Swing("Nadia's Abhorrent Shroud absorbs 579 points of damage from being done to Mempayc.");
        Assert.Equal(579, s.Damage.Number);
        Assert.Null(s.Extra);
    }

    [Fact]
    public void Mana_Refresh_Is_PowerReplenish()
    {
        var s = Swing("Tsuna's Empower Servant refreshes Tsuna for 59 mana points.");
        Assert.Equal(SwingCategory.PowerReplenish, s.Category);
        Assert.Equal(("Tsuna", "Empower Servant", "Tsuna", 59L), (s.Attacker, s.Ability, s.Victim, s.Damage.Number));
    }

    [Fact]
    public void Threat_Increase_And_Reduce()
    {
        var up = Swing("Badbang's Insolent Gibe increases THEIR hate with a dragonspawn whelp for 1,234 threat.");
        Assert.Equal(SwingCategory.Threat, up.Category);
        Assert.Equal(("Badbang", "Insolent Gibe", "a dragonspawn whelp", 1234L), (up.Attacker, up.Ability, up.Victim, up.Damage.Number));
        Assert.Equal("threat", up.DamageType);

        var down = Swing("Noxyi's Dynamism reduces THEIR hate with a blood colossus for 567 threat.");
        Assert.Equal("threat-reduce", down.DamageType);
    }

    [Fact]
    public void Cure_Captures_The_Relieved_Effect()
    {
        var s = Swing("Catofur's Cure Trauma relieves Eviscerate from Badbang.");
        Assert.Equal(SwingCategory.CureDispel, s.Category);
        Assert.Equal(("Catofur", "Cure Trauma", "Badbang"), (s.Attacker, s.Ability, s.Victim));
        Assert.Equal("Eviscerate", s.DamageType); // the cured effect, kept as rich data
        Assert.Equal(DamageValue.NoDamageNumber, s.Damage.Number);
    }

    [Fact]
    public void Zero_Damage_Hit_Counts_As_A_Hit()
    {
        var s = Swing("Shyoh's Vampiric Requiem hits Shyoh but fails to inflict any damage.");
        Assert.Equal(("Shyoh", "Vampiric Requiem", "Shyoh"), (s.Attacker, s.Ability, s.Victim));
        Assert.Equal(DamageValue.NoDamageNumber, s.Damage.Number);
        Assert.Equal(SwingCategory.NonMelee, s.Category);
    }

    [Fact]
    public void FirstPerson_Avoid_Without_Comma()
    {
        var s = Swing("YOU try to pierce Malkonis D'Morte but miss.");
        Assert.Equal(("YOU", "Malkonis D'Morte"), (s.Attacker, s.Victim));
        Assert.Equal(DamageValue.MissNumber, s.Damage.Number);
    }
}
