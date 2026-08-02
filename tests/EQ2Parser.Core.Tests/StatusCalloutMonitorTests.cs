using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Core.Tests;

public class StatusCalloutMonitorTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    private static (StatusCalloutMonitor Monitor, List<(string Effect, int Count)> Fired) Monitor(int min = 3)
    {
        var monitor = new StatusCalloutMonitor { MinVictims = min };
        List<(string, int)> fired = [];
        monitor.Callout += (effect, count) => fired.Add((effect, count));
        return (monitor, fired);
    }

    [Fact]
    public void Wave_Of_Applies_Fires_One_Callout_With_The_Full_Count()
    {
        var (monitor, fired) = Monitor();
        // 8 raiders stunned across 1.5s — threshold crossed at the 3rd,
        // but the collection hold gathers the whole wave first.
        for (var i = 0; i < 8; i++)
            monitor.OnStatusApplied($"Player{i}", "stunned", T0.AddMilliseconds(i * 200));
        monitor.Tick(T0.AddSeconds(0.5));
        Assert.Empty(fired); // still collecting
        monitor.Tick(T0.AddSeconds(2));
        Assert.Equal([("stun", 8)], fired);
    }

    [Fact]
    public void Same_Player_Reapplied_Counts_Once()
    {
        var (monitor, fired) = Monitor();
        monitor.OnStatusApplied("Sofja", "stunned", T0);
        monitor.OnStatusApplied("Sofja", "stunned", T0.AddSeconds(1));
        monitor.OnStatusApplied("Teramo", "stunned", T0.AddSeconds(1));
        monitor.Tick(T0.AddSeconds(3));
        Assert.Empty(fired); // 2 distinct < 3
    }

    [Fact]
    public void Flavour_Words_Map_To_The_Canonical_Effect()
    {
        var (monitor, fired) = Monitor(min: 2);
        // "dazzled" and "mesmerized" are both mez — they pool.
        monitor.OnStatusApplied("Sofja", "mesmerized", T0);
        monitor.OnStatusApplied("Teramo", "dazzled", T0.AddSeconds(1));
        monitor.Tick(T0.AddSeconds(2.5));
        Assert.Equal([("mez", 2)], fired);
    }

    [Fact]
    public void Mob_Victims_Never_Count()
    {
        var (monitor, fired) = Monitor(min: 2);
        monitor.OnStatusApplied("a bloom custodian", "mesmerized", T0);
        monitor.OnStatusApplied("a bisected rumbler", "mesmerized", T0);
        monitor.OnStatusApplied("The Segmented Rumbler", "mesmerized", T0);
        monitor.Tick(T0.AddSeconds(2));
        Assert.Empty(fired);
    }

    [Fact]
    public void Unknown_Flavour_Adjectives_Are_Ignored()
    {
        var (monitor, fired) = Monitor(min: 1);
        monitor.OnStatusApplied("Sofja", "gloomy", T0);
        monitor.Tick(T0.AddSeconds(2));
        Assert.Empty(fired);
    }

    [Fact]
    public void Cooldown_Silences_Repeat_Waves_Then_Recovers()
    {
        var (monitor, fired) = Monitor(min: 2);
        monitor.Cooldown = TimeSpan.FromSeconds(10);
        monitor.OnStatusApplied("Sofja", "stunned", T0);
        monitor.OnStatusApplied("Teramo", "stunned", T0);
        monitor.Tick(T0.AddSeconds(1));
        Assert.Single(fired);

        // Second wave 3s later: inside the cooldown — silent.
        monitor.OnStatusApplied("Sofja", "stunned", T0.AddSeconds(4));
        monitor.OnStatusApplied("Teramo", "stunned", T0.AddSeconds(4));
        monitor.Tick(T0.AddSeconds(6));
        Assert.Single(fired);

        // Third wave after the cooldown expires — fires again.
        monitor.OnStatusApplied("Sofja", "stunned", T0.AddSeconds(15));
        monitor.OnStatusApplied("Teramo", "stunned", T0.AddSeconds(15));
        monitor.Tick(T0.AddSeconds(17));
        Assert.Equal(2, fired.Count);
    }

    [Fact]
    public void Old_Applies_Age_Out_Of_The_Window()
    {
        var (monitor, fired) = Monitor(min: 3);
        monitor.OnStatusApplied("Sofja", "stunned", T0);
        // 10s later two more — the first aged out; 2 distinct < 3.
        monitor.OnStatusApplied("Teramo", "stunned", T0.AddSeconds(10));
        monitor.OnStatusApplied("Nimrael", "stunned", T0.AddSeconds(10));
        monitor.Tick(T0.AddSeconds(12));
        Assert.Empty(fired);
    }

    [Fact]
    public void Every_Canonical_Effect_Has_A_Callout_Word()
    {
        foreach (var effect in Vocabulary.ControlEffects)
            Assert.False(string.IsNullOrWhiteSpace(Vocabulary.CalloutWord(effect)));
    }

    [Fact]
    public void Processor_Raises_StatusApplied_Even_Out_Of_Combat()
    {
        // Pre-pull stuns and no-damage script phases produce status lines
        // with NO running encounter — the callout pipeline used to lose
        // them because the invoke sat behind the encounter gate.
        var engine = new EQ2Parser.Core.Engine.ParserEngine("log-a", "Menludiir");
        var processor = new EQ2Parser.Core.Engine.LogLineProcessor(engine);
        var applied = new List<(string Victim, string Effect)>();
        processor.StatusApplied += (victim, effect, _) => applied.Add((victim, effect));

        Assert.True(EQ2Parser.Core.Logs.LogLine.TryParse(
            $"({T0.ToUnixTimeSeconds()})[s] Sofja is stunned!", out var line));
        processor.Process(line);

        Assert.False(engine.InCombat);
        Assert.Equal(("Sofja", "stunned"), Assert.Single(applied));
    }
}
