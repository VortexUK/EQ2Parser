using System.Text.RegularExpressions;
using EQ2Parser.Core.Logs;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Core.Tests;

public class TriggerEngineTests
{
    private static LogLine Line(string message)
    {
        Assert.True(LogLine.TryParse($"(1753738000)[Mon Jul 28 22:26:40 2026] {message}", out var line));
        return line;
    }

    [Fact]
    public void Fires_On_Match_With_Capture_Expansion()
    {
        var engine = new TriggerEngine();
        engine.SetTriggers([
            new Trigger(
                Id: "cure-curse",
                Pattern: new Regex(@"^(?<victim>\w+) is afflicted by Grim Malediction", RegexOptions.Compiled),
                Tts: "cure ${victim}"),
        ]);

        var fired = new List<TriggerMatch>();
        engine.Fired += fired.Add;

        engine.Process(Line("Menludiir is afflicted by Grim Malediction."));
        engine.Process(Line("You hit a training dummy for 100 points of crushing damage."));

        var match = Assert.Single(fired);
        Assert.Equal("cure-curse", match.Trigger.Id);
        Assert.Equal("cure Menludiir", match.ExpandedTts);
    }

    [Fact]
    public void Multiple_Triggers_Fire_In_Order()
    {
        var engine = new TriggerEngine();
        engine.SetTriggers([
            new Trigger("a", new Regex("dummy")),
            new Trigger("b", new Regex("training")),
            new Trigger("c", new Regex("no-match-here")),
        ]);

        var ids = new List<string>();
        engine.Fired += m => ids.Add(m.Trigger.Id);

        engine.Process(Line("You hit a training dummy for 100 points of crushing damage."));
        Assert.Equal(["a", "b"], ids);
    }

    [Fact]
    public void SetTriggers_Replaces_The_Active_Set()
    {
        var engine = new TriggerEngine();
        engine.SetTriggers([new Trigger("old", new Regex("dummy"))]);
        engine.SetTriggers([new Trigger("new", new Regex("dummy"))]);

        var ids = new List<string>();
        engine.Fired += m => ids.Add(m.Trigger.Id);
        engine.Process(Line("dummy"));

        Assert.Equal(["new"], ids);
    }
}
