using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Core.Tests;

/// <summary>Importing ACT's full settings export (long attribute names,
/// Config/CustomTriggers/SpellTimers sections).</summary>
public class ActConfigImportTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <Config>
            <CustomTriggers>
                <Trigger Active="True" Regex="In the name of the Ancient" SoundData="Stun" SoundType="3" CategoryRestrict="False" Category=" General" Timer="True" TimerName="Ancient" Tabbed="False" />
                <Trigger Active="False" Regex="placed the rally banner in" SoundData="Rally banner placed" SoundType="3" CategoryRestrict="False" Category=" General" Timer="False" TimerName="" Tabbed="False" />
                <Trigger Regex="([unclosed" Category="Broken" />
            </CustomTriggers>
            <SpellTimers>
                <Spell Checked="True" Name="Harm Touch" Timer="60" OnlyMasterTicks="False" Restrict="False" Absolute="False" WarningValue="15" RadialDisplay="False" Modable="True" Tooltip="" FillColor="-16776961" Panel1="True" Panel2="False" RemoveValue="-15" Category=" General" RestrictCategory="False" />
            </SpellTimers>
        </Config>
        """;

    [Fact]
    public void Imports_Triggers_And_Spells_From_A_Full_Config()
    {
        var result = ActConfigImport.TryImport(Sample);
        Assert.NotNull(result);
        Assert.Equal(2, result.Triggers.Count);
        Assert.Equal(1, result.Skipped); // the broken regex
        var timer = Assert.Single(result.Timers);

        var ancient = result.Triggers[0];
        Assert.Equal("In the name of the Ancient", ancient.RegexText);
        Assert.Equal("General", ancient.Category); // " General" trimmed
        Assert.Equal(TriggerSound.Tts, ancient.SoundType);
        Assert.True(ancient.StartsTimer);
        Assert.Equal("Ancient", ancient.TimerName);
        Assert.True(ancient.Enabled);
        Assert.False(result.Triggers[1].Enabled); // Active="False" honoured

        Assert.Equal("Harm Touch", timer.Name);
        Assert.Equal(60, timer.DurationSeconds);
        Assert.Equal(15, timer.WarningSeconds);
    }

    [Fact]
    public void Rejects_Dtd_Carrying_Documents()
    {
        var evil = """
            <!DOCTYPE r [<!ENTITY a "x">]>
            <Config><CustomTriggers><Trigger Regex="&a;" Category="G" /></CustomTriggers></Config>
            """;
        Assert.Null(ActConfigImport.TryImport(evil));
    }

    [Fact]
    public void Garbage_And_TriggerFree_Xml_Return_Null()
    {
        Assert.Null(ActConfigImport.TryImport("not xml"));
        Assert.Null(ActConfigImport.TryImport("<Settings><Other /></Settings>"));
    }
}
