using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Logs;
using Xunit.Abstractions;

namespace EQ2Parser.Core.Tests;

/// <summary>Diagnostic probe: do outgoing buckets ever contain swings the
/// combatant RECEIVED? (env-gated on EQ2PARSER_SAMPLE_LOG)</summary>
public class BucketSeparationProbe(ITestOutputHelper output)
{
    [Fact]
    public void Outgoing_Buckets_Contain_Only_Swings_By_The_Combatant()
    {
        var path = Environment.GetEnvironmentVariable("EQ2PARSER_SAMPLE_LOG");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;
        var owner = Path.GetFileNameWithoutExtension(path).Replace("eq2log_", "").Split('.')[0];
        var engine = new ParserEngine(path, owner);
        var processor = new LogLineProcessor(engine);
        foreach (var raw in File.ReadLines(path))
            if (LogLine.TryParse(raw, out var line))
                processor.Process(line);
        engine.EndCombat();

        var offenders = 0;
        foreach (var encounter in engine.History)
        {
            foreach (var combatant in encounter.Combatants.Values)
            {
                if (!combatant.OutgoingBuckets.TryGetValue(BucketConfig.OutgoingDamage, out var bucket))
                    continue;
                foreach (var swing in bucket.All.Swings)
                {
                    if (!string.Equals(swing.Attacker, combatant.Name, StringComparison.OrdinalIgnoreCase) && offenders++ < 12)
                        output.WriteLine($"[{encounter.Title}] {combatant.Name}: OUT bucket has swing BY '{swing.Attacker}' ability '{swing.Ability}' vs '{swing.Victim}'");
                }
            }
        }
        output.WriteLine($"total misfiled swings: {offenders}");
        Assert.Equal(0, offenders);
    }
}
