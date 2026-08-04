using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;
using EQ2Parser.Core.Engine;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// Multi-log correlation: the headline feature. Two logs of the same fight
/// merge into one canonical encounter with per-combatant authority; separate
/// fights stay separate.
/// </summary>
public class EncounterCorrelatorTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);
    private static DateTimeOffset At(int s) => T0.AddSeconds(s);

    private static ParserEngine Engine(string source, string owner, string zone = "Deathtoll")
    {
        var engine = new ParserEngine(source, owner);
        engine.ChangeZone(zone);
        return engine;
    }

    private static void Hit(ParserEngine e, int at, string attacker, string victim, long dmg)
    {
        Assert.True(e.SetEncounter(At(at), attacker, victim));
        e.AddSwing(SwingCategory.Melee, false, "None", attacker, "Strike", dmg, At(at), victim, "crushing");
    }

    [Fact]
    public void NonPrimarySources_Is_Everything_But_The_Longest()
    {
        var correlator = new EncounterCorrelator();
        var a = Engine("log-a", "Alice");
        var b = Engine("log-b", "Bobette");
        correlator.Attach(a);
        correlator.Attach(b);

        // Alice's window is 0-20s (longest → primary); Bobette's 1-8s.
        Hit(a, 0, "Alice", "Lord Bob", 100);
        Hit(a, 20, "Alice", "Lord Bob", 100);
        Hit(b, 1, "Bobette", "Lord Bob", 50);
        Hit(b, 8, "Bobette", "Lord Bob", 50);
        a.EndCombat();
        b.EndCombat();

        var fight = Assert.Single(correlator.History);
        Assert.Equal("log-a", fight.Primary.SourceId);
        var mirror = Assert.Single(fight.NonPrimarySources);
        Assert.Equal("log-b", mirror.SourceId);
    }

    [Fact]
    public void Two_Logs_Of_The_Same_Fight_Merge_With_PerCombatant_Authority()
    {
        var correlator = new EncounterCorrelator();
        var a = Engine("log-a", "Alice");
        var b = Engine("log-b", "Bobette");
        correlator.Attach(a);
        correlator.Attach(b);
        var events = new List<string>();
        correlator.Created += _ => events.Add("created");
        correlator.Merged += _ => events.Add("merged");

        // Alice's log: her 3 swings fully, one of Bobette's partially.
        Hit(a, 0, "Alice", "Lord Bob", 100);
        Hit(a, 5, "Alice", "Lord Bob", 100);
        Hit(a, 10, "Alice", "Lord Bob", 100);
        Hit(a, 7, "Bobette", "Lord Bob", 50);
        // Bobette's log: her 4 swings fully, one of Alice's partially.
        Hit(b, 1, "Bobette", "Lord Bob", 50);
        Hit(b, 4, "Bobette", "Lord Bob", 50);
        Hit(b, 8, "Bobette", "Lord Bob", 50);
        Hit(b, 12, "Bobette", "Lord Bob", 50);
        Hit(b, 6, "Alice", "Lord Bob", 100);
        a.EndCombat();
        b.EndCombat();

        Assert.Equal(["created", "merged"], events);
        var fight = Assert.Single(correlator.History);
        Assert.Equal(2, fight.Sources.Count);

        var merged = fight.MergedCombatants;
        // Own logs always win for their owners.
        Assert.True(merged["ALICE"].IsSourceOwner);
        Assert.Equal("log-a", merged["ALICE"].AuthoritySourceId);
        Assert.True(merged["BOBETTE"].IsSourceOwner);
        Assert.Equal("log-b", merged["BOBETTE"].AuthoritySourceId);
        // The boss goes to the source that observed him more completely
        // (log-b recorded 5 swings against him, log-a recorded 4).
        Assert.Equal("log-b", merged["LORD BOB"].AuthoritySourceId);
        Assert.False(merged["LORD BOB"].IsSourceOwner);

        // Merged damage beats either single view: Alice 300 + Bobette 200.
        Assert.Equal(500, fight.Damage);
        Assert.True(fight.Damage > fight.Sources.Max(s => s.Damage));
    }

    [Fact]
    public void Different_Zones_Never_Merge()
    {
        var correlator = new EncounterCorrelator();
        var a = Engine("log-a", "Alice", "Deathtoll");
        var b = Engine("log-b", "Bobette", "The Emerald Halls");
        correlator.Attach(a);
        correlator.Attach(b);

        Hit(a, 0, "Alice", "Lord Bob", 100);
        Hit(b, 1, "Bobette", "Lord Bob", 50);
        a.EndCombat();
        b.EndCombat();

        Assert.Equal(2, correlator.History.Count);
    }

    [Fact]
    public void Sequential_Pulls_Beyond_Tolerance_Stay_Separate()
    {
        var correlator = new EncounterCorrelator(new CorrelatorOptions { TimeTolerance = TimeSpan.FromSeconds(30) });
        var a = Engine("log-a", "Alice");
        var b = Engine("log-b", "Bobette");
        correlator.Attach(a);
        correlator.Attach(b);

        Hit(a, 0, "Alice", "a gnoll", 100);
        a.EndCombat();
        Hit(b, 120, "Bobette", "a gnoll", 50); // same mob name, two minutes later
        b.EndCombat();

        Assert.Equal(2, correlator.History.Count);
    }

    [Fact]
    public void A_Source_Never_Merges_With_Itself()
    {
        var correlator = new EncounterCorrelator();
        var a = Engine("log-a", "Alice");
        correlator.Attach(a);

        Hit(a, 0, "Alice", "a gnoll", 100);
        a.EndCombat();
        // Immediate re-pull of an identically named mob, same zone, within
        // tolerance — still a different fight because it's the same log.
        Hit(a, 8, "Alice", "a gnoll", 100);
        a.EndCombat();

        Assert.Equal(2, correlator.History.Count);
    }

    [Fact]
    public void Boxed_Characters_Fighting_Different_Mobs_Stay_Separate()
    {
        var correlator = new EncounterCorrelator();
        var a = Engine("log-a", "Alice");
        var b = Engine("log-b", "Bobette");
        correlator.Attach(a);
        correlator.Attach(b);

        // Side-by-side soloing: each log also sees the other character
        // (buffs/heals), but the mobs differ — only owner names are shared.
        Hit(a, 0, "Alice", "a gnoll", 100);
        Assert.True(a.SetEncounter(At(2), "Bobette", "Alice"));
        a.AddSwing(SwingCategory.Healing, false, "None", "Bobette", "Heal", 10, At(2), "Alice", "heal");
        Hit(b, 1, "Bobette", "a snake", 50);
        Assert.True(b.SetEncounter(At(3), "Alice", "Bobette"));
        b.AddSwing(SwingCategory.Healing, false, "None", "Alice", "Heal", 10, At(3), "Bobette", "heal");
        a.EndCombat();
        b.EndCombat();

        Assert.Equal(2, correlator.History.Count);
    }

    [Fact]
    public void Primary_Is_The_Longest_Source_And_Supplies_The_Title()
    {
        var correlator = new EncounterCorrelator();
        var a = Engine("log-a", "Alice");
        var b = Engine("log-b", "Bobette");
        correlator.Attach(a);
        correlator.Attach(b);

        Hit(a, 0, "Alice", "Lord Bob", 100);
        Hit(a, 30, "Alice", "Lord Bob", 100); // 30s window — the longer view
        Hit(b, 10, "Bobette", "Lord Bob", 50); // 0s window
        a.EndCombat();
        b.EndCombat();

        var fight = Assert.Single(correlator.History);
        Assert.Equal("log-a", fight.Primary.SourceId);
        Assert.Equal("Lord Bob", fight.Title);
        Assert.Equal(At(0), fight.StartTime);
    }

    [Fact]
    public void Restore_Reinserts_Chronologically_And_Is_Idempotent()
    {
        var correlator = new EncounterCorrelator(new CorrelatorOptions { TimeTolerance = TimeSpan.FromSeconds(5) });
        var a = Engine("log-a", "Alice");
        correlator.Attach(a);

        Hit(a, 0, "Alice", "a gnoll", 100);
        a.EndCombat();
        Hit(a, 60, "Alice", "a wolf", 100);
        a.EndCombat();
        Hit(a, 120, "Alice", "a bear", 100);
        a.EndCombat();
        Assert.Equal(3, correlator.History.Count);

        // Delete the middle fight, then undo — it must come back in order.
        var middle = correlator.History[1];
        Assert.True(correlator.Remove(middle));
        Assert.Equal(2, correlator.History.Count);

        correlator.Restore(middle);
        Assert.Equal(3, correlator.History.Count);
        Assert.Same(middle, correlator.History[1]);

        // Restoring a fight already in history is a no-op.
        correlator.Restore(middle);
        Assert.Equal(3, correlator.History.Count);
    }

    [Fact]
    public void Shared_Owner_Name_Across_Sources_Does_Not_Crash_Merge()
    {
        // The same character name can log from two servers ("Bob" on
        // Varsoon and Kaladim) — MergedCombatants used to throw
        // ArgumentException building its owner map, crashing the UI tick.
        var correlator = new EncounterCorrelator();
        var a = Engine("log-varsoon", "Bob");
        var b = Engine("log-kaladim", "Bob");
        correlator.Attach(a);
        correlator.Attach(b);
        Hit(a, 0, "Bob", "Lord Bob", 100);
        Hit(b, 2, "Bob", "Lord Bob", 50);
        a.EndCombat();
        b.EndCombat();

        var fight = Assert.Single(correlator.History);
        var merged = fight.MergedCombatants;
        Assert.True(merged["BOB"].IsSourceOwner);
    }

    [Fact]
    public void Merge_Still_Found_When_History_Is_Out_Of_EndTime_Order()
    {
        // Undo-restores and archive pull-backs insert out of end-time order.
        // The old reverse-walk BREAK stopped at the first early-ending
        // candidate and missed the overlapping fight beyond it.
        var correlator = new EncounterCorrelator(new CorrelatorOptions { TimeTolerance = TimeSpan.FromSeconds(30) });
        var a = Engine("log-a", "Alice");
        correlator.Attach(a);

        // Recent fight lands first…
        Hit(a, 1000, "Alice", "Lord Bob", 100);
        a.EndCombat();
        // …then an OLD fight is appended after it (archive pull-back shape).
        Hit(a, 0, "Alice", "a gnoll", 100);
        a.EndCombat();
        Assert.Equal(2, correlator.History.Count);

        // A second log of the RECENT fight must still merge even though the
        // reverse walk meets the early-ending old fight first.
        var b = Engine("log-b", "Bobette");
        correlator.Attach(b);
        Hit(b, 1005, "Bobette", "Lord Bob", 50);
        b.EndCombat();

        Assert.Equal(2, correlator.History.Count);
        Assert.Contains(correlator.History, f => f.Sources.Count == 2);
    }
}
