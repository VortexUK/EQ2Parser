using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.History;

namespace EQ2Parser.Core.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eq2parser-history-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private HistoryStore Store() => new(Path.Combine(_dir, "history.db"));

    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    private static Encounter BuildEncounter()
    {
        var engine = new ParserEngine("log-a", "Menludiir");
        engine.ChangeZone("Deathtoll");
        Assert.True(engine.SetEncounter(T0, "Menludiir", "Lord Bob"));
        engine.AddSwing(SwingCategory.Melee, true, "Multi Attack", "Menludiir", "Strike", 1500, T0, "Lord Bob", "crushing");
        engine.AddSwing(SwingCategory.Healing, false, "None", "Menludiir", "Ward", 200, T0.AddSeconds(2), "Menludiir", EQ2Parser.Core.Grammar.EnglishGrammar.WardAbsorbType, extra: "remaining=4041");
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menludiir", "Strike", DamageValue.Miss, T0.AddSeconds(4), "Lord Bob", "avoided");
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menludiir", Combatant.KillingAbility, DamageValue.Death, T0.AddSeconds(6), "Lord Bob", "death");
        engine.EndCombat();
        return engine.History[^1];
    }

    [Fact]
    public void Save_And_Query_Summaries()
    {
        using var store = Store();
        var id = store.SaveEncounter(BuildEncounter(), correlationId: "fight-1");
        Assert.True(id > 0);

        var summary = Assert.Single(store.QueryEncounters());
        Assert.Equal(("log-a", "Menludiir", "Deathtoll", "Lord Bob"), (summary.SourceId, summary.Owner, summary.Zone, summary.Title));
        Assert.Equal(SuccessLevel.Win, summary.Success);
        Assert.Equal(1500, summary.Damage);
        Assert.Equal("fight-1", summary.CorrelationId);

        Assert.Empty(store.QueryEncounters(zone: "The Emerald Halls"));
        Assert.Single(store.QueryEncounters(zone: "Deathtoll"));
    }

    [Fact]
    public void Swings_RoundTrip_Including_Sentinels_And_Extra()
    {
        using var store = Store();
        var id = store.SaveEncounter(BuildEncounter());

        var swings = store.LoadSwings(id);
        Assert.Equal(4, swings.Count);

        var crit = swings[0];
        Assert.Equal((SwingCategory.Melee, true, "Multi Attack", 1500L), (crit.Category, crit.Critical, crit.Special, crit.Damage.Number));

        var ward = swings[1];
        Assert.Equal("remaining=4041", ward.Extra);
        Assert.Equal(EQ2Parser.Core.Grammar.EnglishGrammar.WardAbsorbType, ward.DamageType);

        var miss = swings[2];
        Assert.True(miss.Damage.IsMiss); // sentinel equality survives the round-trip

        var death = swings[3];
        Assert.True(death.Damage.IsDeath);
        Assert.Equal(Combatant.KillingAbility, death.Ability);
    }

    [Fact]
    public void Combatant_Summaries_Carry_Ally_Flag()
    {
        using var store = Store();
        var id = store.SaveEncounter(BuildEncounter());

        var combatants = store.LoadCombatants(id);
        var menlu = combatants.Single(c => c.Name == "Menludiir");
        var boss = combatants.Single(c => c.Name == "Lord Bob");
        Assert.True(menlu.IsAlly);
        Assert.False(boss.IsAlly);
        Assert.Equal(1500, menlu.Damage);
        Assert.Equal(200, menlu.Healed);
        Assert.Equal(1, boss.Deaths);
    }

    [Fact]
    public void Delete_Cascades_To_Children()
    {
        using var store = Store();
        var id = store.SaveEncounter(BuildEncounter());
        Assert.True(store.DeleteEncounter(id));
        Assert.Empty(store.QueryEncounters());
        Assert.Empty(store.LoadSwings(id));
        Assert.Empty(store.LoadCombatants(id));
        Assert.False(store.DeleteEncounter(id));
    }

    [Fact]
    public void Reopening_The_Store_Preserves_Data()
    {
        var path = Path.Combine(_dir, "history.db");
        long id;
        using (var store = new HistoryStore(path))
            id = store.SaveEncounter(BuildEncounter());
        using (var store = new HistoryStore(path))
        {
            Assert.Single(store.QueryEncounters());
            Assert.Equal(4, store.LoadSwings(id).Count);
        }
    }

    [Fact]
    public void RestoreEncounter_Rebuilds_A_Drillable_Fight()
    {
        using var store = Store();
        var original = BuildEncounter();
        store.SaveEncounter(original);

        var restored = store.RestoreEncounter(Assert.Single(store.QueryEncounters()));

        // Same identity, totals, outcome, and structure as the live fight.
        Assert.Equal(original.Title, restored.Title);
        Assert.Equal(original.Zone, restored.Zone);
        Assert.Equal(original.Damage, restored.Damage);
        Assert.Equal(original.Healed, restored.Healed);
        Assert.Equal(original.Duration, restored.Duration);
        Assert.Equal(original.GetSuccessLevel(), restored.GetSuccessLevel());
        Assert.Equal(original.Combatants.Count, restored.Combatants.Count);
        Assert.False(restored.Active);
        // The rebuilt buckets drill: the owner's outgoing reference bucket
        // carries the same swing count.
        Assert.Equal(
            original.Combatants["MENLUDIIR"].OutgoingBuckets[BucketConfig.AllOutgoingRef].All.Swings.Count,
            restored.Combatants["MENLUDIIR"].OutgoingBuckets[BucketConfig.AllOutgoingRef].All.Swings.Count);
    }

    [Fact]
    public void PruneBefore_Drops_Old_Fights_And_Keeps_Recent()
    {
        using var store = Store();
        store.SaveEncounter(BuildEncounter());
        Assert.Equal(0, store.PruneBefore(T0)); // fight starts AT T0 — kept
        Assert.Single(store.QueryEncounters());
        Assert.Equal(1, store.PruneBefore(T0.AddDays(1)));
        Assert.Empty(store.QueryEncounters());
    }
}
