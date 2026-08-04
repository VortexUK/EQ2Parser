using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Logs;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// The owner-name aliasing rules, from real-log evidence (2026-08): a real
/// self-death is ALWAYS second person ("has killed you."); a third-person
/// death naming the log owner can only be a temp pet dying under their name
/// (the Templar hammer). ACT counts both as the player — we deviate,
/// deliberately.
/// </summary>
public class DeathAttributionTests
{
    private static (ParserEngine Engine, LogLineProcessor Processor) Harness(string owner = "Menludiir")
    {
        var engine = new ParserEngine($@"C:\logs\Wuoshi\eq2log_{owner}.txt", owner);
        engine.ChangeZone("Castle Mistmoore");
        return (engine, new LogLineProcessor(engine));
    }

    private static void Feed(LogLineProcessor processor, long epoch, string message)
    {
        Assert.True(LogLine.TryParse($"({epoch})[Mon Jul 13 20:06:12 2026] {message}", out var line));
        processor.Process(line);
    }

    private const long T = 1_783_969_572;

    [Fact]
    public void Second_Person_Kill_Is_The_Owners_Real_Death()
    {
        var (engine, processor) = Harness();
        Feed(processor, T, "Menludiir hits Mayong Mistmoore for 100 divine damage.");
        Feed(processor, T + 5, "Mayong Mistmoore has killed you.");
        engine.EndCombat();

        var encounter = engine.History[^1];
        Assert.Equal(1, encounter.Combatants["MENLUDIIR"].Deaths);
        Assert.Equal(0, encounter.Combatants["MENLUDIIR"].PetDeaths);
        // The old case-sensitive Resolve sent this death to a stray
        // combatant literally named "you".
        Assert.False(encounter.Combatants.ContainsKey("YOU"));
    }

    [Fact]
    public void Third_Person_Own_Name_Kill_Is_A_Pet_Death()
    {
        var (engine, processor) = Harness();
        Feed(processor, T, "Menludiir's Divine Smash hits Mayong Mistmoore for 1,717 divine damage.");
        Feed(processor, T + 5, "Mayong Mistmoore has killed Menludiir.");
        engine.EndCombat();

        var owner = engine.History[^1].Combatants["MENLUDIIR"];
        Assert.Equal(0, owner.Deaths);
        Assert.Equal(1, owner.PetDeaths);
        // The killer gets no player-kill credit for swatting a hammer.
        Assert.Equal(0, engine.History[^1].Combatants["MAYONG MISTMOORE"].GetKills(isAlly: false));
    }

    [Fact]
    public void Other_Players_Third_Person_Deaths_Still_Count()
    {
        // The honest boundary: in MY log, "has killed Ariadneh" could be her
        // or her pet — indistinguishable here, so it counts (her own log is
        // the authority that can correct it via per-combatant merge).
        var (engine, processor) = Harness();
        // The owner acts too — a fight with no owner involvement can't seed
        // the ally graph and gets scrapped as an untitled encounter.
        Feed(processor, T, "Menludiir hits Mayong Mistmoore for 100 divine damage.");
        Feed(processor, T + 1, "Ariadneh hits Mayong Mistmoore for 500 disease damage.");
        Feed(processor, T + 5, "Mayong Mistmoore has killed Ariadneh.");
        engine.EndCombat();

        var ariadneh = engine.History[^1].Combatants["ARIADNEH"];
        Assert.Equal(1, ariadneh.Deaths);
        Assert.Equal(0, ariadneh.PetDeaths);
        Assert.Equal(1, engine.History[^1].Combatants["MAYONG MISTMOORE"].GetKills(isAlly: false));
    }

    [Fact]
    public void Hammer_Cadence_Never_Inflates_The_Owner()
    {
        // The real-world shape: hammer summoned and killed over and over —
        // 82 of these in one Mayong night vs 16 real deaths.
        var (engine, processor) = Harness();
        // Damage lines keep the fight inside the 6s idle window — deaths
        // record into a live fight but never extend one.
        for (var i = 0; i < 5; i++)
        {
            Feed(processor, T + i * 5, "Menludiir hits Mayong Mistmoore for 100 divine damage.");
            Feed(processor, T + i * 5 + 1, "Mayong Mistmoore has killed Menludiir.");
        }
        Feed(processor, T + 25, "Menludiir hits Mayong Mistmoore for 100 divine damage.");
        Feed(processor, T + 26, "Mayong Mistmoore has killed you.");
        engine.EndCombat();

        var owner = engine.History[^1].Combatants["MENLUDIIR"];
        Assert.Equal(1, owner.Deaths);
        Assert.Equal(5, owner.PetDeaths);
    }
}
