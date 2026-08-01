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
    public void Import_Accepts_Act_Config_File_Dialect()
    {
        // ACT's config XML / community trigger packs use long attribute
        // names (Regex/SoundData/…/Active) instead of the share snippet's
        // short ones. Both must import.
        var t = Assert.IsType<Trigger>(ActShareFormat.TryImport(
            """<Trigger Active="False" Regex="(?&lt;Player&gt;.+?) placed the rally banner in (?&lt;Location&gt;.+)." SoundData="Flag placed in ${Location}" SoundType="3" CategoryRestrict="False" Category=" General" Timer="False" TimerName="" Tabbed="False" />"""));
        Assert.False(t.Enabled);
        Assert.Equal("(?<Player>.+?) placed the rally banner in (?<Location>.+).", t.RegexText);
        Assert.Equal("General", t.Category); // leading space trimmed
        Assert.Equal(TriggerSound.Tts, t.SoundType);
        Assert.Equal("Flag placed in ${Location}", t.SoundData);
        Assert.False(t.RestrictToCategoryZone);
        Assert.False(t.StartsTimer);

        var t2 = Assert.IsType<Trigger>(ActShareFormat.TryImport(
            """<Trigger Active="True" Regex="In the name of the Ancient" SoundData="Stun" SoundType="3" CategoryRestrict="True" Category="Avatar of Fear" Timer="True" TimerName="Ancient" Tabbed="False" />"""));
        Assert.True(t2.Enabled);
        Assert.True(t2.RestrictToCategoryZone);
        Assert.True(t2.StartsTimer);
        Assert.Equal("Ancient", t2.TimerName);

        // The short share dialect still imports (no Active attr → enabled).
        var t3 = Assert.IsType<Trigger>(ActShareFormat.TryImport(
            """<Trigger R="dragon roars" SD="joust" ST="3" CR="F" C="General" T="F" TN="" Ta="F" />"""));
        Assert.True(t3.Enabled);
        Assert.Equal(TriggerSound.Tts, t3.SoundType);
    }

    [Fact]
    public void Import_Accepts_Act_Config_File_Spell_Dialect()
    {
        // ACT's config XML uses long names for <Spell> too. Checked maps to
        // Enabled; padded categories are trimmed; negative FillColor round-
        // trips; StartWav/WarningWav land in the sound fields verbatim.
        var d = Assert.IsType<TimerDefinition>(ActShareFormat.TryImport(
            """<Spell Checked="False" Name="Acidic Mist" Timer="60" OnlyMasterTicks="False" Restrict="False" Absolute="False" StartWav="" WarningWav="" WarningValue="10" RadialDisplay="False" Modable="True" Tooltip="" FillColor="-16776961" Panel1="True" Panel2="False" RemoveValue="-15" Category=" General" RestrictCategory="False" />"""));
        Assert.False(d.Enabled);
        Assert.Equal("Acidic Mist", d.Name);
        Assert.Equal("General", d.Category);
        Assert.Equal(60, d.DurationSeconds);
        Assert.Equal(10, d.WarningSeconds);
        Assert.Equal(-15, d.RemoveSeconds);
        Assert.Equal(unchecked((int)0xFF0000FF), d.FillColorArgb);
        Assert.False(d.RadialDisplay);
        Assert.True(d.Modable);

        var d2 = Assert.IsType<TimerDefinition>(ActShareFormat.TryImport(
            """<Spell Checked="True" Name="Damage Shield" Timer="46" OnlyMasterTicks="True" Restrict="False" Absolute="False" StartWav="" WarningWav="tts Shield off" WarningValue="11" RadialDisplay="False" Modable="False" Tooltip="" FillColor="-16744448" Panel1="True" Panel2="False" RemoveValue="0" Category=" General" RestrictCategory="False" />"""));
        Assert.True(d2.Enabled);
        Assert.True(d2.OnlyMasterTicks);
        Assert.False(d2.Modable);
        Assert.Equal("tts Shield off", d2.WarningSoundData);
        Assert.Equal(0, d2.RemoveSeconds);

        // Panel flags survive import (sound-only timers are Panel1=False).
        var d4 = Assert.IsType<TimerDefinition>(ActShareFormat.TryImport(
            """<Spell Checked="True" Name="Potion" Timer="900" WarningWav="tts Potion Expired" WarningValue="5" Panel1="False" Panel2="True" RemoveValue="0" Category=" General" />"""));
        Assert.False(d4.Panel1);
        Assert.True(d4.Panel2);

        // Empty Name (ACT's junk default row) is rejected, not imported.
        Assert.Null(ActShareFormat.TryImport(
            """<Spell Checked="True" Name="" Timer="30" Category=" General" />"""));

        // The short share dialect still imports.
        var d3 = Assert.IsType<TimerDefinition>(ActShareFormat.TryImport(
            """<Spell N="Ancient" T="60" OM="F" R="F" A="F" WV="10" RD="F" M="F" Tt="" FC="-16776961" RV="-15" C="General" RC="F" />"""));
        Assert.True(d3.Enabled);
        Assert.Equal(60, d3.DurationSeconds);
    }

    [Fact]
    public void Disabled_Timer_Definition_Never_Starts_A_Bar()
    {
        var timers = new SpellTimerService();
        timers.AddOrUpdateDefinition(new TimerDefinition { Name = "Mayong's Touch", Enabled = false });
        Assert.False(timers.Notify("mayong", "Mayong's Touch", self: false, "nimrael", T0));
        Assert.Empty(timers.Frames);

        timers.AddOrUpdateDefinition(new TimerDefinition { Name = "Mayong's Touch", Enabled = true });
        Assert.True(timers.Notify("mayong", "Mayong's Touch", self: false, "nimrael", T0));
    }

    [Fact]
    public void Status_Effect_Lines_Parse_As_Status_Swings()
    {
        // Apply flavours vary; releases are uniform. Real shapes from live
        // Wuoshi logs.
        var apply = Assert.IsType<Grammar.SwingEvent>(
            Grammar.EnglishGrammar.TryParse("An Awakened trial servant is stunned!"));
        Assert.Equal(Combat.SwingCategory.StatusEffect, apply.Category);
        Assert.Equal("stunned", apply.Ability);
        Assert.Equal("An Awakened trial servant", apply.Victim);
        Assert.Equal("applied", apply.DamageType);

        var flavoured = Assert.IsType<Grammar.SwingEvent>(
            Grammar.EnglishGrammar.TryParse("A sentinel of the Trial is unnerved by a stare!"));
        Assert.Equal("unnerved", flavoured.Ability);

        var totally = Assert.IsType<Grammar.SwingEvent>(
            Grammar.EnglishGrammar.TryParse("An Awakened trial servant is totally confused!"));
        Assert.Equal("confused", totally.Ability);

        var release = Assert.IsType<Grammar.SwingEvent>(
            Grammar.EnglishGrammar.TryParse("An Awakened trial servant is no longer stunned."));
        Assert.Equal("stunned", release.Ability);
        Assert.Equal("released", release.DamageType);

        // Effect-specific flavours found in the 1.2 GB archive sweep.
        Assert.Equal("frozen", Assert.IsType<Grammar.SwingEvent>(
            Grammar.EnglishGrammar.TryParse("A charred diviner is briefly frozen in place!")).Ability);
        Assert.Equal("terrified", Assert.IsType<Grammar.SwingEvent>(
            Grammar.EnglishGrammar.TryParse("Claw looks terrified!")).Ability);
        Assert.Equal("gloomy", Assert.IsType<Grammar.SwingEvent>(
            Grammar.EnglishGrammar.TryParse("Slaverjaw is consumed by gloom!")).Ability);
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
