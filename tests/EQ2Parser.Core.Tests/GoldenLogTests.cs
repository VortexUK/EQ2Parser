using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Logs;
using Xunit.Abstractions;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// The golden harness seed: run the full pipeline (LogLine → grammar →
/// engine) over a REAL log file and report coverage + encounter stats.
/// Machine-specific, so it's driven by the EQ2PARSER_SAMPLE_LOG env var and
/// skipped when unset (CI). The character name is taken from the filename
/// (eq2log_&lt;name&gt;.txt), matching the game's layout.
/// </summary>
public class GoldenLogTests(ITestOutputHelper output)
{
    [Fact]
    public void Full_Pipeline_Over_A_Real_Log()
    {
        var path = Environment.GetEnvironmentVariable("EQ2PARSER_SAMPLE_LOG");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            output.WriteLine("EQ2PARSER_SAMPLE_LOG not set or missing — golden run skipped.");
            return;
        }
        var owner = Path.GetFileNameWithoutExtension(path).Replace("eq2log_", "").Split('.')[0];

        var engine = new ParserEngine(path, owner);
        var processor = new LogLineProcessor(engine);

        long total = 0, parseable = 0;
        foreach (var raw in File.ReadLines(path))
        {
            total++;
            if (LogLine.TryParse(raw, out var line))
            {
                parseable++;
                processor.Process(line);
            }
        }
        engine.EndCombat();

        output.WriteLine($"owner: {owner}");
        output.WriteLine($"lines: {total:N0} | valid log lines: {parseable:N0}");
        output.WriteLine($"grammar matched: {processor.LinesMatched:N0} ({100.0 * processor.LinesMatched / Math.Max(1, processor.LinesSeen):F1}% of lines)");
        output.WriteLine($"encounters: {engine.History.Count:N0}");

        var titled = engine.History.Count(e => e.Title != EQ2Parser.Core.Combat.Encounter.PlaceholderTitle);
        output.WriteLine($"titled encounters: {titled:N0}");
        foreach (var enc in engine.History.Where(e => e.Duration.TotalSeconds >= 15).OrderByDescending(e => e.Damage).Take(10))
            output.WriteLine($"  [{enc.Zone}] {enc.Title} — {enc.Duration.TotalSeconds:F0}s, dmg {enc.Damage:N0}, encdps {enc.EncDps:N0}, success {enc.GetSuccessLevel()}");

        Assert.True(engine.History.Count > 0, "expected at least one encounter from a real log");
        Assert.True(processor.LinesMatched > 0);
    }
}
