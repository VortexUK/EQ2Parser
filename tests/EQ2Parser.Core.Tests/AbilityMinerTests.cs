using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.History;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// The archive-mining maths behind timer curation: hit→volley clustering
/// (2s), volley→application chaining (12s), median recast intervals, the
/// swipe-adjusted base interval, anonymous-hit attribution, and detriment
/// detection. This logic feeds curated timer durations — it was untested
/// while it lived in the App project.
/// </summary>
public class AbilityMinerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);
    private static DateTimeOffset At(double s) => T0.AddSeconds(s);

    private static int _sorter;

    private static Swing Hit(
        double at, string attacker, string ability, string victim, long dmg,
        SwingCategory category = SwingCategory.NonMelee, string school = "poison") =>
        new(category, false, "None", attacker, ability, dmg, At(at), ++_sorter, victim, school);

    private static EncounterSummary Summary(long id, string title = "Lord Bob") =>
        new(id, "log", "Alice", "Deathtoll", title, T0, T0.AddMinutes(5), 300, SuccessLevel.Win, 1, null, IsBoss: true);

    private static (EncounterSummary, IReadOnlyList<Swing>, HashSet<string>) Fight(
        long id, IReadOnlyList<Swing> swings, params string[] enemies) =>
        (Summary(id), swings, new HashSet<string>(enemies, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void MultiTarget_Volley_Is_One_Cast_And_Intervals_Are_Measured()
    {
        // Boss AoE hits 3 raiders within 2s → ONE volley; recast 30s apart.
        List<Swing> swings = [];
        foreach (var castAt in new[] { 0.0, 30.0, 60.0 })
        {
            swings.Add(Hit(castAt, "Lord Bob", "Stench", "Alice", 100));
            swings.Add(Hit(castAt + 0.5, "Lord Bob", "Stench", "Bea", 100));
            swings.Add(Hit(castAt + 1.0, "Lord Bob", "Stench", "Cara", 100));
        }
        var mobs = AbilityMiner.MineZone([Fight(1, swings, "Lord Bob")], "Deathtoll");

        var ability = Assert.Single(Assert.Single(mobs).Abilities);
        Assert.Equal(3, ability.Casts);
        Assert.Equal(30, ability.MedianIntervalSeconds);
        Assert.Equal(30, ability.MinIntervalSeconds);
        Assert.Equal(3, ability.AvgTargets); // 9 hits over 3 volleys
        Assert.Equal(900, ability.TotalDamage);
    }

    [Fact]
    public void Dot_Ticks_Chain_Into_One_Application()
    {
        // Ticks every 6s (within the 12s application chain) are ONE cast;
        // the next application starts 60s later.
        List<Swing> swings = [];
        foreach (var castAt in new[] { 0.0, 60.0 })
        {
            for (var tick = 0; tick < 4; tick++)
                swings.Add(Hit(castAt + tick * 6, "Lord Bob", "Noxious Cloud", "Alice", 50));
        }
        var mobs = AbilityMiner.MineZone([Fight(1, swings, "Lord Bob")], "Deathtoll");

        var ability = Assert.Single(Assert.Single(mobs).Abilities);
        Assert.Equal(2, ability.Casts);
        Assert.Equal(60, ability.MedianIntervalSeconds);
        Assert.Equal(4, ability.TicksPerCast); // 8 volleys over 2 applications
    }

    [Fact]
    public void Traumatic_Swipe_Windows_Discount_The_Base_Interval()
    {
        // Casts 45s apart, with the mob under Traumatic Swipe (50% recast
        // slow, 30s duration) for the first 30s of the interval: base
        // interval = 15 + 30/1.5 = 35s.
        List<Swing> swings =
        [
            Hit(0, "Alice", "Traumatic Swipe", "Lord Bob", 10, SwingCategory.Melee, "slashing"),
            Hit(0, "Lord Bob", "Cleave", "Alice", 100),
            Hit(45, "Lord Bob", "Cleave", "Alice", 100),
        ];
        var mobs = AbilityMiner.MineZone([Fight(1, swings, "Lord Bob")], "Deathtoll");

        var cleave = Assert.Single(Assert.Single(mobs).Abilities, a => a.Ability == "Cleave");
        Assert.Equal(45, cleave.MedianIntervalSeconds);
        Assert.NotNull(cleave.BaseIntervalSeconds);
        Assert.Equal(35, cleave.BaseIntervalSeconds!.Value, precision: 3);
        Assert.Equal(30.0 / 45.0, cleave.SwipeCoverage, precision: 3);
    }

    [Fact]
    public void Anonymous_Hits_Attribute_To_The_Fight_Title_And_Are_Marked_Inferred()
    {
        List<Swing> swings =
        [
            Hit(0, "Unknown", "Stench of Death", "Alice", 4205),
            Hit(30, "Unknown", "Stench of Death", "Alice", 4205),
        ];
        // "Unknown" must be listed as an enemy for its swings to qualify.
        var mobs = AbilityMiner.MineZone([Fight(1, swings, "Lord Bob", "Unknown")], "Deathtoll");

        var mob = Assert.Single(mobs);
        Assert.Equal("Lord Bob", mob.Mob);
        var ability = Assert.Single(mob.Abilities);
        Assert.True(ability.SourceInferred);
        Assert.Equal(2, ability.Casts);
    }

    [Fact]
    public void AllZero_Hits_Flag_A_Detriment_And_AutoAttacks_Are_Ignored()
    {
        List<Swing> swings =
        [
            // Auto-attacks are noise, never timer material.
            Hit(0, "Lord Bob", Grammar.EnglishGrammar.AutoAttackAbility, "Alice", 500, SwingCategory.Melee, "crushing"),
            // A control effect that never deals damage.
            Hit(1, "Lord Bob", "Terrifying Gaze", "Alice", 0),
            Hit(31, "Lord Bob", "Terrifying Gaze", "Alice", 0),
        ];
        var mobs = AbilityMiner.MineZone([Fight(1, swings, "Lord Bob")], "Deathtoll");

        var ability = Assert.Single(Assert.Single(mobs).Abilities);
        Assert.Equal("Terrifying Gaze", ability.Ability);
        Assert.True(ability.IsDetriment);
    }

    [Fact]
    public void Damage_Type_Shares_Report_Meaningful_Schools_Biggest_First()
    {
        List<Swing> swings = [];
        for (var i = 0; i < 9; i++)
            swings.Add(Hit(i * 30, "Lord Bob", "Twin Venom", "Alice", 100, school: "poison"));
        for (var i = 0; i < 6; i++)
            swings.Add(Hit(i * 30 + 5, "Lord Bob", "Twin Venom", "Alice", 100, school: "disease"));
        // A one-off school under the 10% share threshold is dropped.
        swings.Add(Hit(500, "Lord Bob", "Twin Venom", "Alice", 100, school: "magic"));

        var mobs = AbilityMiner.MineZone([Fight(1, swings, "Lord Bob")], "Deathtoll");
        var ability = Assert.Single(Assert.Single(mobs).Abilities);
        Assert.Equal("poison, disease", ability.DamageTypes);
    }

    [Fact]
    public void Intervals_Never_Cross_Fight_Boundaries()
    {
        // One cast in each of two fights: no within-fight interval exists,
        // so no recast can be claimed — the gap between fights is not data.
        var fight1 = Fight(1, [Hit(0, "Lord Bob", "Cleave", "Alice", 100)], "Lord Bob");
        var fight2 = Fight(2, [Hit(600, "Lord Bob", "Cleave", "Alice", 100)], "Lord Bob");
        var mobs = AbilityMiner.MineZone([fight1, fight2], "Deathtoll");

        var ability = Assert.Single(Assert.Single(mobs).Abilities);
        Assert.Equal(2, ability.Fights);
        Assert.Null(ability.MedianIntervalSeconds);
    }
}
