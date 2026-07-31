using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Logs;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Core.Tests;

public class TriggerEngineTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    private static (TriggerEngine Engine, List<TriggerFired> Fired) Engine(params Trigger[] triggers)
    {
        var engine = new TriggerEngine("Menludiir");
        foreach (var t in triggers)
            engine.AddOrUpdate(t);
        var fired = new List<TriggerFired>();
        engine.Fired += fired.Add;
        return (engine, fired);
    }

    [Fact]
    public void Fires_With_Tts_Capture_Expansion()
    {
        var (engine, fired) = Engine(new Trigger(@"^(?<victim>\w+) is afflicted by Grim Malediction")
        {
            SoundType = TriggerSound.Tts,
            SoundData = "cure ${victim}",
        });

        engine.Process("Sofja is afflicted by Grim Malediction.", T0);
        engine.Process("You hit a training dummy for 100 points of crushing damage.", T0);

        var f = Assert.Single(fired);
        Assert.Equal("cure Sofja", f.TtsText);
    }

    [Fact]
    public void YOU_Group_Only_Fires_For_The_Owner()
    {
        var (engine, fired) = Engine(new Trigger(@"^(?<YOU>\w+) is stunned")
        {
            SoundType = TriggerSound.Beep,
        });

        engine.Process("Sofja is stunned by the blast.", T0);
        Assert.Empty(fired);
        engine.Process("Menludiir is stunned by the blast.", T0.AddSeconds(5));
        Assert.Single(fired);
        Assert.True(fired[0].PlayBeep);
    }

    [Fact]
    public void Audio_Rate_Limit_Suppresses_Rapid_Refires()
    {
        var (engine, fired) = Engine(new Trigger("dragon roars")
        {
            SoundType = TriggerSound.Beep,
        });

        engine.Process("The dragon roars!", T0);
        engine.Process("The dragon roars!", T0.AddMilliseconds(400));
        engine.Process("The dragon roars!", T0.AddSeconds(2));

        Assert.Equal(3, fired.Count); // fires every time…
        Assert.Equal([true, false, true], fired.Select(f => f.PlayBeep)); // …audio only outside the cooldown
    }

    [Fact]
    public void Zone_Restriction_Is_A_Substring_With_Instance_Stripped()
    {
        var trigger = new Trigger("joust now", "Kaeldun")
        {
            RestrictToCategoryZone = true,
            SoundType = TriggerSound.Beep,
        };
        var (engine, fired) = Engine(trigger);

        engine.Process("joust now", T0);
        Assert.Empty(fired); // no zone yet

        engine.SetZone("Kaeldun Keep 2");
        engine.Process("joust now", T0.AddSeconds(5));
        Assert.Single(fired);

        engine.SetZone("The Emerald Halls");
        engine.Process("joust now", T0.AddSeconds(10));
        Assert.Single(fired); // inactive again
    }

    [Fact]
    public void Timer_Request_Reads_Attacker_And_Victim_Groups()
    {
        var (engine, fired) = Engine(new Trigger(@"(?<attacker>\w+) begins casting Doom on (?<victim>\w+)")
        {
            StartsTimer = true,
            TimerName = "Doom",
        });

        engine.Process("Bossmob begins casting Doom on Sofja!", T0);
        var timer = Assert.Single(fired).Timer;
        Assert.Equal(new TimerRequest("Doom", "Bossmob", "Sofja"), timer);
    }

    [Fact]
    public void AddOrUpdate_Replaces_By_Identity_Key_Immediately()
    {
        var (engine, fired) = Engine(new Trigger("dragon roars") { SoundType = TriggerSound.Beep });
        engine.AddOrUpdate(new Trigger("dragon roars") { SoundType = TriggerSound.Tts, SoundData = "roar" });

        engine.Process("The dragon roars!", T0);
        var f = Assert.Single(fired);
        Assert.Equal("roar", f.TtsText);
        Assert.False(f.PlayBeep);
    }

    [Fact]
    public void Prefilter_Never_False_Negatives()
    {
        // A pattern with a solid literal gets a prefilter…
        Assert.NotNull(new Trigger("Grim Malediction").PrefilterLiteral);
        // …anchored/grouped/alternation patterns fall back to always-regex.
        Assert.Null(new Trigger("^(a|b)$").PrefilterLiteral);
        // Quantified chars never leak into the literal.
        var t = new Trigger(@"roa?rs loudly");
        Assert.NotNull(t.PrefilterLiteral);
        Assert.DoesNotContain("roa", t.PrefilterLiteral);

        var (engine, fired) = Engine(new Trigger(@"^(?:the )?dragon roars$") { SoundType = TriggerSound.Beep });
        engine.Process("dragon roars", T0);
        Assert.Single(fired);
    }

    [Fact]
    public void Processor_Skips_Triggers_For_Replayed_History()
    {
        // Parse-from-start replays hours of old lines in seconds — those
        // must never reach the trigger engine (no beep/TTS spam), while a
        // live tail (arrival ≈ log time) and stamp-less feeds always do.
        var engine = new ParserEngine("log", "Menludiir");
        var triggers = new TriggerEngine("Menludiir");
        var fired = new List<TriggerFired>();
        triggers.Fired += fired.Add;
        triggers.AddOrUpdate(new Trigger("dragon roars") { SoundType = TriggerSound.Beep });
        var processor = new LogLineProcessor(engine, triggers);

        Assert.True(LogLine.TryParse($"({T0.ToUnixTimeSeconds()})[s] The dragon roars!", out var line));

        // Replayed: observed long after it was written.
        processor.Process(line with { ObservedAt = T0 + LogLineProcessor.TriggerFreshness + TimeSpan.FromSeconds(1) });
        Assert.Empty(fired);

        // Live: observed moments after it was written.
        processor.Process(line with { ObservedAt = T0.AddMilliseconds(250) });
        Assert.Single(fired);

        // No arrival stamp (direct feed / tests): treated as live.
        processor.Process(line);
        Assert.Equal(2, fired.Count);
    }
}
