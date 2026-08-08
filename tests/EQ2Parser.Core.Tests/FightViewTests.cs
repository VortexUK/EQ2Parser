using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;
using EQ2Parser.Core.Engine;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// The shape-free fight surface: Encounter and CorrelatedEncounter agree on
/// the same view contract, and DisplaySeconds is the single display-rate
/// clamp (uploads keep the real duration via EncDps).
/// </summary>
public class FightViewTests
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
    public void Encounter_View_Surface_Matches_Its_Concrete_Stats()
    {
        var a = Engine("log-a", "Alice");
        Hit(a, 0, "Alice", "Lord Bob", 100);
        Hit(a, 10, "Alice", "Lord Bob", 100);
        a.EndCombat();
        var encounter = Assert.Single(a.History);

        IFightView view = encounter;
        Assert.Equal(encounter.Title, view.Title);
        Assert.Equal(encounter.Zone, view.Zone);
        Assert.Equal(encounter.Duration, view.Duration);
        Assert.Equal(encounter.Damage, view.Damage);
        Assert.Equal(encounter.EncDps, view.EncDps);
        Assert.Same(encounter, view.ClassificationSource);
        Assert.Same(encounter, Assert.Single(view.ClassificationSources));
        Assert.True(view.ContainsCombatant("ALICE"));
        Assert.False(view.ContainsCombatant("NOBODY"));
        Assert.Single(view.InstancesOf("ALICE"));
        Assert.Empty(view.InstancesOf("NOBODY"));
        Assert.Contains(view.ViewCombatants, kv => kv.Key == "LORD BOB");
        // Allies exclude the enemy.
        Assert.DoesNotContain(view.AllyCombatants, kv => kv.Key == "LORD BOB");
        Assert.Contains(view.AllyCombatants, kv => kv.Key == "ALICE");
    }

    [Fact]
    public void Merged_View_Reads_From_The_Authoritative_Merge()
    {
        var correlator = new EncounterCorrelator();
        var a = Engine("log-a", "Alice");
        var b = Engine("log-b", "Bobette");
        correlator.Attach(a);
        correlator.Attach(b);
        Hit(a, 0, "Alice", "Lord Bob", 100);
        Hit(a, 10, "Alice", "Lord Bob", 100);
        Hit(b, 1, "Bobette", "Lord Bob", 50);
        a.EndCombat();
        b.EndCombat();
        var fight = Assert.Single(correlator.History);

        IFightView view = fight;
        Assert.Same(fight.Primary, view.ClassificationSource);
        Assert.Same(fight.Primary, Assert.Single(view.ClassificationSources));
        Assert.True(view.ContainsCombatant("BOBETTE"));
        Assert.Single(view.InstancesOf("BOBETTE"));
        // The merged view carries every combatant exactly once.
        Assert.Equal(fight.MergedCombatants.Count, view.ViewCombatants.Count());
        // Ally view = merged combatants ∩ merged ally keys.
        Assert.Contains(view.AllyCombatants, kv => kv.Key == "ALICE");
        Assert.Contains(view.AllyCombatants, kv => kv.Key == "BOBETTE");
        Assert.DoesNotContain(view.AllyCombatants, kv => kv.Key == "LORD BOB");
        Assert.Equal(fight.Damage, view.Damage);
    }

    [Fact]
    public void DisplaySeconds_Clamps_Zero_Duration_But_EncDps_Keeps_The_Real_Rule()
    {
        // A same-second fight: whole-second log timestamps make 0 the only
        // sub-1 duration. Display maths clamp to 1; the upload-parity EncDps
        // stays 0 (act-behavior.md §3 — no invented duration in the numbers).
        var a = Engine("log-a", "Alice");
        Hit(a, 0, "Alice", "a gnoll", 500);
        a.EndCombat();
        var encounter = Assert.Single(a.History);

        IFightView view = encounter;
        Assert.Equal(TimeSpan.Zero, view.Duration);
        Assert.Equal(1, view.DisplaySeconds);
        Assert.Equal(0, view.EncDps);

        // A normal fight reports its real seconds.
        var b = Engine("log-b", "Bobette");
        Hit(b, 0, "Bobette", "a gnoll", 100);
        Hit(b, 20, "Bobette", "a gnoll", 100);
        b.EndCombat();
        IFightView longer = Assert.Single(b.History);
        Assert.Equal(20, longer.DisplaySeconds);
    }
}
