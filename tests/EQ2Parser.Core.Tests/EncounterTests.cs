using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// Pins the encounter/stat semantics from docs/act-behavior.md §§2-3 through
/// the engine's public contract (grammar-eye view: SetEncounter + AddSwing).
/// </summary>
public class EncounterTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);
    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    private static ParserEngine Engine() => new("test-log", "Menludiir");

    private static void Swing(
        ParserEngine engine, int atSeconds, SwingCategory category, string attacker, string victim,
        long damage, string ability = "Strike", bool critical = false, string damageType = "crushing")
    {
        Assert.True(engine.SetEncounter(At(atSeconds), attacker, victim));
        engine.AddSwing(category, critical, "None", attacker, ability, damage, At(atSeconds), victim, damageType);
    }

    private static void Death(ParserEngine engine, int atSeconds, string killer, string victim)
    {
        Assert.True(engine.SetEncounter(At(atSeconds), killer, victim));
        engine.AddSwing(SwingCategory.Melee, false, "None", killer, Combatant.KillingAbility,
            DamageValue.Death, At(atSeconds), victim, "death");
    }

    [Fact]
    public void Only_Damage_Starts_Or_Extends_A_Fight()
    {
        var engine = Engine();

        // A heal out of combat is dropped — no junk encounter.
        Assert.False(engine.SetEncounter(At(0), "Sofja", "Menludiir", hostile: false));
        Assert.False(engine.InCombat);

        // Fight starts on damage; post-fight healing does NOT extend it —
        // the idle clock runs from the last HOSTILE swing, so the next
        // line past the timeout closes the fight even if heals landed
        // in between (the "one encounter bleeding into another" bug).
        Swing(engine, 10, SwingCategory.Melee, "Menludiir", "a gnoll", 100);
        Assert.True(engine.SetEncounter(At(13), "Sofja", "Menludiir", hostile: false));
        engine.AddSwing(SwingCategory.Healing, false, "None", "Sofja", "Mend", 50, At(13), "Menludiir", "heal");

        engine.OnLineTime(At(17)); // 7s after the damage, 4s after the heal
        Assert.False(engine.InCombat);

        // The closed fight is cut back to its last damaging swing.
        Assert.Equal(At(10), engine.History[0].EndTime);

        // The next pull opens a NEW encounter instead of bleeding in.
        Swing(engine, 17, SwingCategory.Melee, "Menludiir", "a second gnoll", 100);
        Assert.Equal(2, engine.History.Count);
    }

    [Fact]
    public void Placeholder_Titled_Scraps_Are_Discarded()
    {
        // A fight with no identifiable enemy (here: the log owner never
        // participates, so the ally graph is empty and no title resolves)
        // is dropped from history on end — ACT discards these too.
        var engine = Engine();
        var ended = 0;
        engine.EncounterEnded += _ => ended++;
        Swing(engine, 0, SwingCategory.Melee, "SomeoneElse", "AnotherGuy", 100);
        engine.EndCombat();
        Assert.Empty(engine.History);
        Assert.Equal(0, ended);

        // A real fight still survives and announces.
        Swing(engine, 10, SwingCategory.Melee, "Menludiir", "a gnoll", 100);
        engine.EndCombat();
        Assert.Single(engine.History);
        Assert.Equal("a gnoll", engine.History[0].Title);
        Assert.Equal(1, ended);
    }

    [Fact]
    public void Self_Damage_Counts_As_Taken_Not_Outgoing()
    {
        // Lifetap procs ("Menludiir's Vampiric Requiem hits Menludiir for
        // 373 focus damage.") must not inflate the caster's own DPS — ACT
        // records them on the taken side only. Self-heals still count out.
        var engine = Engine();
        Swing(engine, 0, SwingCategory.Melee, "Menludiir", "a gnoll", 100);
        Swing(engine, 1, SwingCategory.NonMelee, "Menludiir", "Menludiir", 373, "Vampiric Requiem", damageType: "focus");
        engine.AddSwing(SwingCategory.Healing, false, "None", "Menludiir", "Reverence", 50, At(2), "Menludiir", "heal");
        engine.EndCombat();

        var menlu = engine.History[^1].Combatants["MENLUDIIR"];
        Assert.Equal(100, menlu.Damage);
        Assert.Equal(373, menlu.DamageTaken);
        Assert.Equal(50, menlu.Healed);
        Assert.False(menlu.OutgoingBuckets[BucketConfig.OutgoingDamage].Abilities.ContainsKey("Vampiric Requiem"));
    }

    [Fact]
    public void AddSwing_Without_SetEncounter_Throws()
    {
        var engine = Engine();
        Assert.Throws<InvalidOperationException>(() =>
            engine.AddSwing(SwingCategory.Melee, false, "None", "A", "Hit", 1, At(0), "B", "crushing"));
    }

    [Fact]
    public void Outgoing_Damage_Lands_In_Two_Buckets_Incoming_In_One()
    {
        var engine = Engine();
        Swing(engine, 0, SwingCategory.Melee, "Menludiir", "a training dummy", 100);

        var attacker = engine.ActiveEncounter!.Combatants["MENLUDIIR"];
        Assert.Equal(100, attacker.OutgoingBuckets[BucketConfig.AutoAttackOut].All.Damage);
        Assert.Equal(100, attacker.OutgoingBuckets[BucketConfig.OutgoingDamage].All.Damage);
        Assert.Equal(100, attacker.OutgoingBuckets[BucketConfig.AllOutgoingRef].All.Damage);
        // Damage property reads the aggregate — no double count.
        Assert.Equal(100, attacker.Damage);

        var victim = engine.ActiveEncounter.Combatants["A TRAINING DUMMY"];
        Assert.Equal(100, victim.DamageTaken);
        Assert.False(victim.IncomingBuckets.ContainsKey(BucketConfig.AutoAttackOut));
    }

    [Fact]
    public void Zero_Damage_Counts_As_Hit_And_Avoids_Exclude_Death()
    {
        var engine = Engine();
        Swing(engine, 0, SwingCategory.Melee, "Menludiir", "mob one", 0, ability: "Jab");
        Assert.True(engine.SetEncounter(At(1), "Menludiir", "mob one"));
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menludiir", "Jab", DamageValue.Miss, At(1), "mob one", "crushing");
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menludiir", "Jab", new DamageValue(-3), At(2), "mob one", "crushing"); // parry
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menludiir", "Jab", DamageValue.Death, At(3), "mob one", "death");

        var jab = engine.ActiveEncounter!.Combatants["MENLUDIIR"]
            .OutgoingBuckets[BucketConfig.AutoAttackOut].Abilities["Jab"];
        Assert.Equal(1, jab.Hits);      // the 0-damage swing IS a hit
        Assert.Equal(1, jab.Misses);
        Assert.Equal(1, jab.Avoids);    // parry; death excluded
        Assert.Equal(3, jab.SwingCount); // death excluded from swings
    }

    [Fact]
    public void Trailing_Heal_Does_Not_Extend_The_Encounter()
    {
        var engine = Engine();
        Swing(engine, 0, SwingCategory.Melee, "Menludiir", "a gnoll", 500);
        Swing(engine, 10, SwingCategory.Melee, "Menludiir", "a gnoll", 500);
        // Heal 5 seconds after the last damaging swing.
        Swing(engine, 15, SwingCategory.Healing, "Menludiir", "Menludiir", 999, ability: "Reverence", damageType: "heal");
        engine.EndCombat();

        var encounter = engine.History[^1];
        // Window = first action → last ALLY DAMAGING swing (t=10), not the heal.
        Assert.Equal(At(0), encounter.StartTime);
        Assert.Equal(At(10), encounter.EndTime);
        Assert.Equal(TimeSpan.FromSeconds(10), encounter.Duration);
        // EncDPS = ally damage / encounter duration.
        Assert.Equal(100, encounter.EncDps, precision: 5);
    }

    [Fact]
    public void Allies_Resolve_By_Sign_Propagation_From_The_Owner()
    {
        var engine = Engine();
        // Menludiir hits the gnoll; Sofja heals Menludiir; the gnoll hits Sofja.
        Swing(engine, 0, SwingCategory.Melee, "Menludiir", "a gnoll", 100);
        Swing(engine, 1, SwingCategory.Healing, "Sofja", "Menludiir", 50, ability: "Heal", damageType: "heal");
        Swing(engine, 2, SwingCategory.Melee, "a gnoll", "Sofja", 25);
        engine.EndCombat();

        var allies = engine.History[^1].GetAllies().Select(a => a.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["Menludiir", "Sofja"], allies);
    }

    [Fact]
    public void Encounter_Without_The_Owner_Has_No_Allies_And_Indeterminate_Success()
    {
        var engine = Engine();
        Swing(engine, 0, SwingCategory.Melee, "Somebodyelse", "a gnoll", 100);
        var encounter = engine.ActiveEncounter!;
        engine.EndCombat();

        Assert.Empty(encounter.GetAllies());
        Assert.Equal(0, encounter.Damage);
        Assert.Equal(Encounter.PlaceholderTitle, encounter.Title);
        Assert.Equal(SuccessLevel.Indeterminate, encounter.GetSuccessLevel());
        // …and precisely because it resolved no title, it was discarded.
        Assert.Empty(engine.History);
    }

    [Fact]
    public void Title_Is_Strongest_Enemy_By_DamageTaken_Per_Death()
    {
        var engine = Engine();
        // Add A takes 1000 across 2 deaths (score 500); boss takes 900, no deaths (score 900).
        Swing(engine, 0, SwingCategory.Melee, "Menludiir", "an underling", 1000);
        Death(engine, 1, "Menludiir", "an underling");
        Death(engine, 2, "Menludiir", "an underling");
        Swing(engine, 3, SwingCategory.NonMelee, "Menludiir", "Lord Bob", 900, ability: "Smite");
        engine.EndCombat();

        Assert.Equal("Lord Bob", engine.History[^1].Title);
    }

    [Fact]
    public void Success_Levels_Follow_EnemyDied_And_AllySurvived()
    {
        // Win: enemy died, ally (player-shaped name, 0 deaths) survived.
        var win = Engine();
        Swing(win, 0, SwingCategory.Melee, "Menludiir", "Lord Bob", 100);
        Death(win, 1, "Menludiir", "Lord Bob");
        win.EndCombat();
        Assert.Equal(SuccessLevel.Win, win.History[^1].GetSuccessLevel());

        // Partial: enemy died but every ally died too.
        var partial = Engine();
        Swing(partial, 0, SwingCategory.Melee, "Menludiir", "Lord Bob", 100);
        Death(partial, 1, "Menludiir", "Lord Bob");
        Death(partial, 2, "Lord Bob", "Menludiir");
        partial.EndCombat();
        Assert.Equal(SuccessLevel.Partial, partial.History[^1].GetSuccessLevel());

        // Loss: nobody killed the enemy and no ally survived.
        var loss = Engine();
        Swing(loss, 0, SwingCategory.Melee, "Menludiir", "Lord Bob", 100);
        Death(loss, 1, "Lord Bob", "Menludiir");
        loss.EndCombat();
        Assert.Equal(SuccessLevel.Loss, loss.History[^1].GetSuccessLevel());
    }

    [Fact]
    public void Idle_Timeout_Ends_The_Encounter_On_The_Next_Line()
    {
        var engine = Engine();
        var ended = new List<Encounter>();
        engine.EncounterEnded += ended.Add;

        Swing(engine, 0, SwingCategory.Melee, "Menludiir", "a gnoll", 100);
        // A line 5s later: within the 6s idle limit — still in combat.
        engine.OnLineTime(At(5));
        Assert.True(engine.InCombat);
        // A line 7s after the last hostile action: past the limit.
        engine.OnLineTime(At(7));
        Assert.False(engine.InCombat);
        Assert.Single(ended);
        // The closed encounter got its real title.
        Assert.Equal("a gnoll", ended[0].Title);
    }

    [Fact]
    public void Kill_Credit_Space_Heuristic_Applies_To_Non_Allies_Only()
    {
        var engine = Engine();
        Swing(engine, 0, SwingCategory.Melee, "Menludiir", "a gnoll", 100);
        Death(engine, 1, "Menludiir", "a gnoll");          // ally kills NPC → credit
        Death(engine, 2, "a gnoll", "Menludiir");           // enemy kills player-shaped → credit
        Death(engine, 3, "a gnoll", "some other npc");      // enemy kills space-named → no credit
        engine.EndCombat();

        var encounter = engine.History[^1];
        Assert.Equal(1, encounter.Combatants["MENLUDIIR"].GetKills(isAlly: true));
        Assert.Equal(1, encounter.Combatants["A GNOLL"].GetKills(isAlly: false));
    }

    [Fact]
    public void Personal_Window_Differs_From_Encounter_Window()
    {
        var engine = Engine();
        Swing(engine, 0, SwingCategory.Melee, "Menludiir", "a gnoll", 600);
        Swing(engine, 10, SwingCategory.Melee, "Menludiir", "a gnoll", 600);
        Swing(engine, 4, SwingCategory.Melee, "Sofja", "a gnoll", 100);
        Swing(engine, 6, SwingCategory.Melee, "Sofja", "a gnoll", 100);
        engine.EndCombat();

        var encounter = engine.History[^1];
        var sofja = encounter.Combatants["SOFJA"];
        // Personal duration: 4s→6s = 2s. Encounter duration: 0→10 = 10s.
        Assert.Equal(TimeSpan.FromSeconds(2), sofja.Duration);
        Assert.Equal(TimeSpan.FromSeconds(10), encounter.Duration);
        // EncDPS uses the encounter window: 200/10.
        Assert.Equal(20, encounter.EncDpsOf(sofja), precision: 5);
    }
}
