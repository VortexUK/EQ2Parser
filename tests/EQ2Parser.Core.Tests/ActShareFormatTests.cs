using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Core.Tests;

/// <summary>The compatibility promise: ACT share snippets import losslessly.</summary>
public class ActShareFormatTests
{
    [Fact]
    public void Imports_A_Trigger_Snippet()
    {
        var xml = """<Trigger R="(?&lt;YOU&gt;\w+) is afflicted by Grim Malediction" SD="cure ${YOU}" ST="3" CR="F" C="General" T="F" TN="" Ta="F" />""";
        var trigger = Assert.IsType<Trigger>(ActShareFormat.TryImport(xml));
        Assert.Equal(@"(?<YOU>\w+) is afflicted by Grim Malediction", trigger.RegexText);
        Assert.Equal(TriggerSound.Tts, trigger.SoundType);
        Assert.Equal("cure ${YOU}", trigger.SoundData);
        Assert.Equal("General", trigger.Category);
        Assert.False(trigger.RestrictToCategoryZone);
        Assert.False(trigger.StartsTimer);
    }

    [Fact]
    public void Rejects_Dtd_Snippet_Blocking_Billion_Laughs()
    {
        // A pasted "snippet" carrying a DTD (internal-entity expansion bomb,
        // or external-entity file read on other runtimes) must be rejected,
        // not parsed. Real ACT snippets never carry a DTD.
        var evil = """
            <!DOCTYPE r [<!ENTITY a "xxxxxxxxxx"><!ENTITY b "&a;&a;&a;&a;&a;">]>
            <Trigger R="&b;" ST="1" C="General" T="F" TN="" Ta="F" />
            """;
        Assert.Null(ActShareFormat.TryImport(evil));
    }

    [Fact]
    public void Imports_Act_Escaped_Regex_Entities()
    {
        // ACT ships \s as &#92;s and # as &#35; — XML decoding restores them.
        var xml = """<Trigger R="joust&#92;s&#35;now" SD="" ST="1" CR="T" C="Kaeldun" T="T" TN="Joust" Ta="F" />""";
        var trigger = Assert.IsType<Trigger>(ActShareFormat.TryImport(xml));
        Assert.Equal(@"joust\s#now", trigger.RegexText);
        Assert.True(trigger.RestrictToCategoryZone);
        Assert.Equal(("Joust", true), (trigger.TimerName, trigger.StartsTimer));
    }

    [Fact]
    public void Rejects_Snippets_Missing_Identity_Attributes()
    {
        Assert.Null(ActShareFormat.TryImport("""<Trigger SD="x" ST="1" />"""));
        Assert.Null(ActShareFormat.TryImport("""<Spell T="30" />"""));
        Assert.Null(ActShareFormat.TryImport("not xml at all"));
        Assert.Null(ActShareFormat.TryImport("""<Config Xml="..." />"""));
    }

    [Fact]
    public void Invalid_Regex_Is_Rejected_Not_Thrown()
    {
        Assert.Null(ActShareFormat.TryImport("""<Trigger R="([unclosed" C="General" />"""));
    }

    [Fact]
    public void Imports_A_Spell_Snippet_With_Defaults_For_Missing_Attrs()
    {
        var xml = """<Spell N="Harm Touch" C="General" T="60" WV="15" />""";
        var def = Assert.IsType<TimerDefinition>(ActShareFormat.TryImport(xml));
        Assert.Equal("Harm Touch", def.Name);
        Assert.Equal(60, def.DurationSeconds);
        Assert.Equal(15, def.WarningSeconds);
        Assert.Equal(-15, def.RemoveSeconds); // default
        Assert.True(def.Modable);             // default
        // Zone-qualified identity (empty zone for ACT imports).
        Assert.Equal("|general|harm touch", def.Key);
    }

    [Fact]
    public void Trigger_Export_Import_RoundTrips()
    {
        var original = new Trigger(@"(?<victim>\w+) is encased in ice\s#2", "Velious")
        {
            SoundType = TriggerSound.WavFile,
            SoundData = @"C:\sounds\alarm.wav",
            RestrictToCategoryZone = true,
            StartsTimer = true,
            TimerName = "Ice Block",
        };
        var back = Assert.IsType<Trigger>(ActShareFormat.TryImport(ActShareFormat.Export(original)));
        Assert.Equal(original.RegexText, back.RegexText);
        Assert.Equal(original.Key, back.Key);
        Assert.Equal(original.SoundData, back.SoundData);
        Assert.Equal(original.SoundType, back.SoundType);
        Assert.Equal(original.RestrictToCategoryZone, back.RestrictToCategoryZone);
        Assert.Equal((original.TimerName, original.StartsTimer), (back.TimerName, back.StartsTimer));
    }

    [Fact]
    public void Zone_Round_Trips_On_Both_Elements_Via_Z_Attribute()
    {
        // Z is our extension — ACT ignores unknown attributes, so exports
        // stay ACT-pasteable while zone filing survives our own round-trip.
        var trigger = new Trigger("Come forth my brethren", "Malkonis D'Morte", "Freethinker Hideout");
        var back = Assert.IsType<Trigger>(ActShareFormat.TryImport(ActShareFormat.Export(trigger)));
        Assert.Equal("Freethinker Hideout", back.Zone);
        Assert.Equal(trigger.Key, back.Key);

        var timer = new TimerDefinition { Name = "Rumbling of Earth", Category = "a bisected rumbler", Zone = "The Emerald Halls" };
        var timerBack = Assert.IsType<TimerDefinition>(ActShareFormat.TryImport(ActShareFormat.Export(timer)));
        Assert.Equal("The Emerald Halls", timerBack.Zone);
        Assert.Equal(timer.Key, timerBack.Key);
    }

    [Fact]
    public void Spell_Export_Import_RoundTrips()
    {
        var original = new TimerDefinition
        {
            Name = "Divine Aura",
            Category = "priest",
            DurationSeconds = 45,
            WarningSeconds = 5,
            RemoveSeconds = -10,
            RestrictToMe = true,
            OnlyMasterTicks = true,
            FillColorArgb = unchecked((int)0xFF00FF00),
            Tooltip = "pop it <now> & live",
            StartSoundData = "start.wav",
        };
        var back = Assert.IsType<TimerDefinition>(ActShareFormat.TryImport(ActShareFormat.Export(original)));
        Assert.Equal(original with { }, back with { });
    }

    [Fact]
    public void Spell_Export_Preserves_Enabled_And_Panel_Routing()
    {
        // Export used to drop Checked/Panel1/Panel2 (which import honours):
        // export→import re-enabled disabled timers and reset panel routing.
        var original = new TimerDefinition
        {
            Name = "Disabled One",
            Category = "test",
            Enabled = false,
            Panel1 = false,
            Panel2 = true,
        };
        var back = Assert.IsType<TimerDefinition>(ActShareFormat.TryImport(ActShareFormat.Export(original)));
        Assert.False(back.Enabled);
        Assert.False(back.Panel1);
        Assert.True(back.Panel2);
    }
}
