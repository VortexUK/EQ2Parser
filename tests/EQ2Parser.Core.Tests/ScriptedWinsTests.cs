using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Logs;

namespace EQ2Parser.Core.Tests;

/// <summary>Scripted-win detection — bosses that end by say line, not
/// death (ToNT Palace Overseer / Queen Lenya Thex). Fixture lines are
/// verbatim from live Wuoshi logs.</summary>
public class ScriptedWinsTests
{
    private const string OverseerLine =
        """\aNPC 65832 Palace Overseer:Palace Overseer\/a says, "This cannot be!!" """;

    private const string QueenLine =
        """\aNPC 93181 Queen Lenya Thex:Queen Lenya Thex\/a says, "They are just too powerful, my lord! My only hope rests with you, my love, Mayong." """;

    private const string WeakenedLine =
        """\aNPC 1895896 a weakened Lenya Thex:a weakened Lenya Thex\/a says, "*gasp* ... I cannot believe I have been overwhelmed. I... I am losing... my strength." """;

    [Theory]
    [InlineData(OverseerLine, true)]
    [InlineData(QueenLine, true)]
    [InlineData(WeakenedLine, true)]
    // A PLAYER quoting the phrase in chat must not count.
    [InlineData("""\aPC 224973 Moro:Moro\/a says, "This cannot be!!" """, false)]
    // A different NPC using a similar phrase must not count.
    [InlineData("""\aNPC 60858 Kuabu the Songkeeper:Kuabu the Songkeeper\/a says to you, "No!  This cannot be!  All these years..." """, false)]
    // The Queen's ordinary mid-fight dialogue must not count.
    [InlineData("""\aNPC 93181 Queen Lenya Thex:Queen Lenya Thex\/a says, "Let the Harbinger of Absolution be the judgment of those that dare to enter my kingdom!" """, false)]
    // Plain combat lines never match.
    [InlineData("Mayong Mistmoore hits Sofja for 5,000 divine damage.", false)]
    public void Matches_Only_The_Curated_Lines(string message, bool expected) =>
        Assert.Equal(expected, ScriptedWins.Default.TryMatch(message));

    private static string Raw(long epoch, string message) =>
        $"({epoch})[Sat Aug 1 20:44:48 2026] {message}";

    [Fact]
    public void Say_Line_During_The_Fight_Makes_It_A_Win()
    {
        var engine = new ParserEngine("log", "Sofja");
        var processor = new LogLineProcessor(engine);
        string[] lines =
        [
            Raw(100, "YOU hit Palace Overseer for 5,000 piercing damage."),
            Raw(101, "Palace Overseer hits YOU for 2,000 divine damage."),
            Raw(102, OverseerLine.TrimEnd()),
            Raw(103, "YOU hit Palace Overseer for 1,000 piercing damage."),
        ];
        foreach (var raw in lines)
        {
            Assert.True(LogLine.TryParse(raw, out var line), raw);
            processor.Process(line);
        }
        engine.EndCombat();

        var fight = engine.History[^1];
        Assert.True(fight.ScriptedWin);
        // No enemy death anywhere — the heuristic alone would NOT call Win.
        Assert.Equal(SuccessLevel.Win, fight.GetSuccessLevel());
    }

    [Fact]
    public void Say_Line_Outside_A_Fight_Is_Ignored()
    {
        var engine = new ParserEngine("log", "Sofja");
        var processor = new LogLineProcessor(engine);
        Assert.True(LogLine.TryParse(Raw(100, OverseerLine.TrimEnd()), out var line));
        processor.Process(line);
        Assert.Null(engine.ActiveEncounter);
        Assert.Empty(engine.History);
    }

    [Fact]
    public void Restored_Win_Verdict_Survives_The_Swing_Replay()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hist-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new History.HistoryStore(path);
            var engine = new ParserEngine("log", "Sofja");
            var processor = new LogLineProcessor(engine);
            Assert.True(LogLine.TryParse(Raw(100, "YOU hit Palace Overseer for 5,000 piercing damage."), out var hit));
            processor.Process(hit);
            Assert.True(LogLine.TryParse(Raw(101, OverseerLine.TrimEnd()), out var say));
            processor.Process(say);
            engine.EndCombat();
            var fight = engine.History[^1];
            Assert.Equal(SuccessLevel.Win, fight.GetSuccessLevel());

            store.SaveEncounter(fight);
            var summary = Assert.Single(store.SearchEncounters(
                since: DateTimeOffset.FromUnixTimeSeconds(0), bossOnly: true, limit: 10));
            Assert.Equal(SuccessLevel.Win, summary.Success);
            var restored = store.RestoreEncounter(summary);
            Assert.Equal(SuccessLevel.Win, restored.GetSuccessLevel());
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
