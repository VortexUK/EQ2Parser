using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Logs;
using Xunit.Abstractions;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// Golden harness: run the full pipeline (LogLine → grammar → engine) over
/// log files. Two tiers:
///   * the COMMITTED corpus (tests/logs/eq2log_Menludiir.txt — every line a
///     real-log shape, one line per grammar family) runs unconditionally
///     with hard assertions, so CI always exercises the full pipeline;
///   * a machine-local REAL log via the EQ2PARSER_SAMPLE_LOG env var adds
///     bulk coverage reporting when present (skipped in CI).
/// The character name comes from the filename (eq2log_&lt;name&gt;.txt),
/// matching the game's layout.
/// </summary>
public class GoldenLogTests(ITestOutputHelper output)
{
    private static (ParserEngine Engine, LogLineProcessor Processor, long Total, long Parseable) RunPipeline(string path)
    {
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
        return (engine, processor, total, parseable);
    }

    [Fact]
    public void Committed_Corpus_Full_Pipeline()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "logs", "eq2log_Menludiir.txt");
        Assert.True(File.Exists(path), $"committed golden corpus missing: {path}");
        var (engine, processor, total, _) = RunPipeline(path);

        // Coverage: every combat/zone line matched; only the 5 known noise
        // lines (logging-on, 3 chat, the sputters-and-dies suppression) miss.
        Assert.Equal(30, total);
        Assert.Equal(30, processor.LinesSeen);
        Assert.Equal(25, processor.LinesMatched);

        // Two fights: idle-ended trash, then the boss (ended by the trailing
        // chat line's idle check — the 6s rule, exercised end to end).
        Assert.Equal(2, engine.History.Count);

        var trash = engine.History[0];
        Assert.Equal("a gnoll", trash.Title);
        Assert.Equal("Qeynos Province District", trash.Zone);
        Assert.Equal(SuccessLevel.Win, trash.GetSuccessLevel());
        // Duration runs to the kill line — the Killing bookkeeping swing is
        // a damaging outgoing action (ACT semantics), so 710→714.
        Assert.Equal(250, trash.Damage);
        Assert.Equal(4, trash.Duration.TotalSeconds);
        Assert.Equal(1, trash.Combatants["MENLUDIIR"].GetKills(isAlly: true));

        var boss = engine.History[1];
        Assert.Equal("Malkonis D'Morte", boss.Title);
        Assert.Equal("Castle Mistmoore", boss.Zone);
        Assert.Equal(SuccessLevel.Win, boss.GetSuccessLevel());
        // Ally damage: Menludiir 15,032+2,378+5,529 + Asame 140.6K+4,540
        // + Duress 1,234 — heals/wards/threat/anonymous-Unknown excluded.
        Assert.Equal(169_313, boss.Damage);
        // 740 → the 764 slain line (its Killing swing closes the window).
        Assert.Equal(24, boss.Duration.TotalSeconds);
        Assert.Equal(boss.Damage / 24.0, boss.EncDps, precision: 3);
        // Ward absorbs count as healing; the K/M expansion reached stats.
        Assert.Equal(300, boss.Combatants["MEXXY"].Healed);
        Assert.Equal(145_140, boss.Combatants["ASAME"].Damage);
        // The Alas death landed on the ally; the owner survived.
        Assert.Equal(1, boss.Combatants["SOFJA"].Deaths);
        Assert.Equal(0, boss.Combatants["MENLUDIIR"].Deaths);
        // Anonymous damage attributed to the Unknown pseudo-combatant, which
        // is present but never an ally.
        Assert.True(boss.Combatants.ContainsKey(Combatant.UnknownKey));
        Assert.DoesNotContain(boss.GetAllies(), c => c.Key == Combatant.UnknownKey);
    }

    [Fact]
    public void Full_Pipeline_Over_A_Real_Log()
    {
        var path = Environment.GetEnvironmentVariable("EQ2PARSER_SAMPLE_LOG");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            output.WriteLine("EQ2PARSER_SAMPLE_LOG not set or missing — bulk golden run skipped (the committed corpus above always runs).");
            return;
        }
        var (engine, processor, total, parseable) = RunPipeline(path);

        output.WriteLine($"owner: {engine.OwnerName}");
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
