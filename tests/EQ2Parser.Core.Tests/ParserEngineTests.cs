using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// The ACT lifecycle contract (docs/engine-behaviour.md §2) — the compatibility
/// surface site rankings depend on: the 6s idle rule, hostile-only encounter
/// starts, the AddSwing-requires-SetEncounter throw, and placeholder-fight
/// discard.
/// </summary>
public class ParserEngineTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);
    private static DateTimeOffset At(double s) => T0.AddSeconds(s);

    private static ParserEngine Engine(EngineOptions? options = null)
    {
        var engine = new ParserEngine("log", "Alice", options);
        engine.ChangeZone("Deathtoll");
        return engine;
    }

    private static void Hit(ParserEngine e, double at, string attacker = "Alice", string victim = "a gnoll", long dmg = 100)
    {
        Assert.True(e.SetEncounter(At(at), attacker, victim));
        e.AddSwing(SwingCategory.Melee, false, "None", attacker, "Strike", dmg, At(at), victim, "crushing");
    }

    [Fact]
    public void Idle_Rule_Ends_The_Fight_After_Six_Seconds_Of_Silence()
    {
        var engine = Engine();
        var ended = new List<Encounter>();
        engine.EncounterEnded += ended.Add;

        Hit(engine, 0);
        engine.OnLineTime(At(6)); // exactly 6s — NOT over the threshold
        Assert.True(engine.InCombat);
        engine.OnLineTime(At(6.5)); // > 6s of hostile silence
        Assert.False(engine.InCombat);
        Assert.Single(ended);
    }

    [Fact]
    public void NonHostile_Actions_Never_Start_A_Fight_And_Never_Extend_One()
    {
        var engine = Engine();

        // A heal out of combat is dropped (SetEncounter refuses).
        Assert.False(engine.SetEncounter(At(0), "Bea", "Alice", hostile: false));
        Assert.False(engine.InCombat);

        // In combat, non-hostile actions record but do not touch the idle
        // clock: hostile at t=0, heal at t=5 — the fight still idles out
        // 6s after the HOSTILE action, not the heal.
        Hit(engine, 0);
        Assert.True(engine.SetEncounter(At(5), "Bea", "Alice", hostile: false));
        engine.AddSwing(SwingCategory.Healing, false, "None", "Bea", "Mend", 50, At(5), "Alice", "heal");
        engine.OnLineTime(At(6.5));
        Assert.False(engine.InCombat);
    }

    [Fact]
    public void AddSwing_Throws_When_Not_In_Combat()
    {
        var engine = Engine();
        Assert.Throws<InvalidOperationException>(() =>
            engine.AddSwing(SwingCategory.Melee, false, "None", "Alice", "Strike", 100, At(0), "a gnoll", "crushing"));
    }

    [Fact]
    public void Placeholder_Titled_Fights_Are_Discarded_Silently()
    {
        // A fight whose title never resolves past "Encounter" (no
        // identifiable enemy — e.g. only owner-less scraps) is dropped from
        // history and never announced.
        var engine = new ParserEngine("log", "Alice");
        var ended = 0;
        engine.EncounterEnded += _ => ended++;

        // Alice swings at... Alice's ally? Construct a no-enemy fight: the
        // owner never participates, so the ally graph is empty and no
        // strongest enemy resolves.
        Assert.True(engine.SetEncounter(At(0), "Stranger", "Other Stranger"));
        engine.AddSwing(SwingCategory.Melee, false, "None", "Stranger", "Strike", 100, At(0), "Other Stranger", "crushing");
        engine.EndCombat();

        Assert.Empty(engine.History);
        Assert.Equal(0, ended);
    }

    [Fact]
    public void Configured_Idle_Seconds_Are_Honoured()
    {
        var engine = Engine(new EngineOptions { IdleEndSeconds = 10 });
        Hit(engine, 0);
        engine.OnLineTime(At(8));
        Assert.True(engine.InCombat, "8s < a 10s idle window");
        engine.OnLineTime(At(10.5));
        Assert.False(engine.InCombat);
    }

    [Fact]
    public void Zone_Change_Does_Not_End_Combat()
    {
        var engine = Engine();
        Hit(engine, 0);
        engine.ChangeZone("The Emerald Halls");
        Assert.True(engine.InCombat, "ACT semantics: zone lines alone never end a fight");
        // But the NEXT fight carries the new zone.
        engine.EndCombat();
        Hit(engine, 20);
        Assert.Equal("The Emerald Halls", engine.ActiveEncounter!.Zone);
    }

    [Fact]
    public void Completed_Fights_Keep_Their_Titles_And_Order()
    {
        var engine = Engine();
        Hit(engine, 0, victim: "a gnoll");
        engine.AddSwing(SwingCategory.Melee, false, "None", "a gnoll", "Bite", 10, At(1), "Alice", "piercing");
        engine.EndCombat();
        Hit(engine, 20, victim: "a wolf");
        engine.AddSwing(SwingCategory.Melee, false, "None", "a wolf", "Bite", 10, At(21), "Alice", "piercing");
        engine.EndCombat();

        Assert.Equal(["a gnoll", "a wolf"], engine.History.Select(e => e.Title));
    }
}
