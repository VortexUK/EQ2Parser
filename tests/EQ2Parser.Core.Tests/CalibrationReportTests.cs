using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Logs;
using Xunit.Abstractions;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// ACT-diff calibration instrumentation: for the biggest fights in a real
/// log, dump the ingredients of GetSuccessLevel so divergences from ACT's
/// scoring are diagnosable (first target: raid kills reading Partial where
/// ACT scored Win). Env-gated like the golden tests.
/// </summary>
public class CalibrationReportTests(ITestOutputHelper output)
{
    [Fact]
    public void Success_Level_Ingredients_For_Big_Fights()
    {
        var path = Environment.GetEnvironmentVariable("EQ2PARSER_SAMPLE_LOG");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            output.WriteLine("EQ2PARSER_SAMPLE_LOG not set — skipped.");
            return;
        }
        var owner = Path.GetFileNameWithoutExtension(path).Replace("eq2log_", "").Split('.')[0];
        var engine = new ParserEngine(path, owner);
        var processor = new LogLineProcessor(engine);
        foreach (var raw in File.ReadLines(path))
            if (LogLine.TryParse(raw, out var line))
                processor.Process(line);
        engine.EndCombat();

        foreach (var enc in engine.History.Where(e => e.Duration.TotalSeconds >= 120).OrderByDescending(e => e.Damage).Take(8))
        {
            var allies = enc.GetAllies();
            var deathless = allies.Where(a => a.Deaths == 0 && Swing.LooksLikePlayer(a.Name)).Select(a => a.Name).ToArray();
            var bossName = enc.GetStrongestEnemy();
            var boss = bossName is null ? null : enc.Combatants.GetValueOrDefault(bossName.ToUpperInvariant());
            output.WriteLine($"[{enc.Zone}] {enc.Title} — {enc.Duration.TotalSeconds:F0}s, success {enc.GetSuccessLevel()}");
            output.WriteLine($"   boss deaths: {boss?.Deaths ?? -1} | allies: {allies.Count} | deathless player-shaped allies: {deathless.Length}");
            output.WriteLine($"   ally sample: {string.Join(", ", allies.Take(8).Select(a => $"{a.Name}({a.Deaths})"))}");
            if (deathless.Length is > 0 and <= 6)
                output.WriteLine($"   deathless: {string.Join(", ", deathless)}");
        }
    }
}
