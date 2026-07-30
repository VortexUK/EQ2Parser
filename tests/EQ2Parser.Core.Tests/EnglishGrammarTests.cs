using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Grammar;

namespace EQ2Parser.Core.Tests;

/// <summary>Every literal here is a real line from a Varsoon TLE log.</summary>
public class EnglishGrammarTests
{
    private static SwingEvent Swing(string message)
    {
        var parsed = EnglishGrammar.TryParse(message);
        return Assert.IsType<SwingEvent>(parsed);
    }

    [Fact]
    public void Your_Ability_Crit()
    {
        var s = Swing("YOUR Divine Strike hits a bog slug for a critical of 15,032 divine damage.");
        Assert.Equal(("YOU", "Divine Strike", "a bog slug"), (s.Attacker, s.Ability, s.Victim));
        Assert.Equal(15032, s.Damage.Number);
        Assert.True(s.Critical);
        Assert.Equal(SwingCategory.NonMelee, s.Category);
        Assert.Equal("divine", s.DamageType);
    }

    [Fact]
    public void You_AutoAttack_Is_Melee()
    {
        var s = Swing("YOU hit a lesser shadowbone skeleton for a critical of 5,529 crushing damage.");
        Assert.Equal(("YOU", EnglishGrammar.AutoAttackAbility), (s.Attacker, s.Ability));
        Assert.Equal(SwingCategory.Melee, s.Category);
        Assert.Equal(5529, s.Damage.Number);
    }

    [Fact]
    public void ThirdPerson_Plain_AutoAttack()
    {
        var s = Swing("Guard Moor hits a bog slug hatchling for 368 slashing damage.");
        Assert.Equal(("Guard Moor", EnglishGrammar.AutoAttackAbility, "a bog slug hatchling"), (s.Attacker, s.Ability, s.Victim));
        Assert.Equal(SwingCategory.Melee, s.Category);
        Assert.False(s.Critical);
    }

    [Fact]
    public void ThirdPerson_Possessive_Ability()
    {
        var s = Swing("Badbang's Magic Feedback hits Delhagin the Frightful for 14 magic damage.");
        Assert.Equal(("Badbang", "Magic Feedback", "Delhagin the Frightful"), (s.Attacker, s.Ability, s.Victim));
        Assert.Equal(SwingCategory.NonMelee, s.Category);
    }

    [Fact]
    public void MultiAttack_Verb_Becomes_Special()
    {
        var s = Swing("Menludiir's unswerving hammer multi attacks a krait patriarch for a critical of 2,378 crushing damage.");
        Assert.Equal("Menludiir", s.Attacker);
        Assert.Equal("unswerving hammer", s.Ability);
        Assert.Equal("Multi Attack", s.Special);
        Assert.Equal(SwingCategory.Melee, s.Category);
        Assert.True(s.Critical);
    }

    [Theory]
    [InlineData("a krait patriarch tries to pierce YOU, but misses.", DamageValue.MissNumber)]
    [InlineData("a krait patriarch tries to crush Menludiir, but Menludiir parries.", DamageValue.ParryNumber)]
    [InlineData("a Rime advancer tries to slash YOU, but YOU riposte.", DamageValue.RiposteNumber)]
    [InlineData("a Rime advancer tries to crush YOU, but YOU block.", DamageValue.BlockNumber)]
    [InlineData("a Thurgadin esper tries to smash YOU, but YOU resist.", DamageValue.ResistNumber)]
    public void Avoid_Outcomes_Map_To_Sentinel_Codes(string line, long expected)
    {
        var s = Swing(line);
        Assert.Equal(expected, s.Damage.Number);
        Assert.NotEqual("", s.Victim);
        Assert.Equal(EnglishGrammar.AutoAttackAbility, s.Ability);
    }

    [Fact]
    public void Weapon_Can_Be_The_Avoiding_Actor()
    {
        // Real line: the victim's weapon parries; the victim is still Menludiir.
        var s = Swing("a krait patriarch tries to crush Menludiir, but Menludiir's unswerving hammer parries.");
        Assert.Equal("a krait patriarch", s.Attacker);
        Assert.Equal("Menludiir", s.Victim);
        Assert.Equal(DamageValue.ParryNumber, s.Damage.Number);
    }

    [Fact]
    public void Heals()
    {
        var mine = Swing("YOUR Reverence heals YOU for 13 hit points.");
        Assert.Equal((SwingCategory.Healing, "YOU", "Reverence", "YOU", 13L), (mine.Category, mine.Attacker, mine.Ability, mine.Victim, mine.Damage.Number));

        var theirs = Swing("Sofja's Grim Sorcery heals Menludiir for a critical of 1,024 hit points.");
        Assert.Equal(("Sofja", "Grim Sorcery", "Menludiir"), (theirs.Attacker, theirs.Ability, theirs.Victim));
        Assert.True(theirs.Critical);
    }

    [Fact]
    public void Death_Lines()
    {
        var killed = Assert.IsType<DeathEvent>(EnglishGrammar.TryParse("You have killed a glacial tunneler."));
        Assert.Equal(("YOU", "a glacial tunneler"), (killed.Killer, killed.Victim));

        var alas = Assert.IsType<DeathEvent>(EnglishGrammar.TryParse("Alas, a Thurgadin watcher has died from pain and suffering."));
        Assert.Equal(("Unknown", "a Thurgadin watcher"), (alas.Killer, alas.Victim));

        var slain = Assert.IsType<DeathEvent>(EnglishGrammar.TryParse("Delhagin the Frightful has been slain by Menludiir!"));
        Assert.Equal(("Menludiir", "Delhagin the Frightful"), (slain.Killer, slain.Victim));
    }

    [Fact]
    public void Zone_Line()
    {
        var zone = Assert.IsType<ZoneEvent>(EnglishGrammar.TryParse("You have entered Qeynos Province District."));
        Assert.Equal("Qeynos Province District", zone.ZoneName);
    }

    [Theory]
    [InlineData("\\aPC -1 Obeax:Obeax\\/a tells General (2), \"hits for days\"")]
    [InlineData("You say, \"Hail, Stissa, Emissary of the Speaker\"")]
    [InlineData("Logging to 'logs/Varsoon/eq2log_Menludiir.txt' is now *ON*")]
    public void Chat_And_System_Lines_Do_Not_Parse(string message)
    {
        Assert.Null(EnglishGrammar.TryParse(message));
    }
}
