using EQ2Parser.Core.Correlation;

namespace EQ2Parser.App.Services;

/// <summary>One row of the mini parse: rank, name, metric value, share of
/// the top row, and the detected class (for colouring).</summary>
public sealed record MiniParseRow(
    int Rank, string Name, string? ClassName, double Value, double Fraction, long Total);

/// <summary>Header + rows for the mini parse window.</summary>
public sealed record MiniParseData(
    string Title, string DurationLabel, string MetricLabel, IReadOnlyList<MiniParseRow> Rows);

/// <summary>
/// Builds the mini parse view of the CURRENT fight (the newest correlated
/// encounter — live while it runs, lingering after it ends, exactly how a
/// meter should behave). Class detection is cached per fight and refreshed
/// every couple of seconds so a snapshot at 4 Hz stays cheap.
/// </summary>
public sealed class MiniParseSnapshot(SourceManager manager)
{
    private CorrelatedEncounter? _cachedFight;
    private Dictionary<string, string?> _classNames = [];
    private DateTimeOffset _classesRefreshed;

    public MiniParseData Build(int maxRows, string metric)
    {
        lock (manager.Sync)
        {
            var fight = manager.Correlator.History.Count > 0 ? manager.Correlator.History[^1] : null;
            if (fight is null)
                return new MiniParseData("Waiting for combat…", "", metric, []);

            var now = DateTimeOffset.Now;
            if (!ReferenceEquals(fight, _cachedFight) || now - _classesRefreshed > TimeSpan.FromSeconds(2))
            {
                _cachedFight = fight;
                _classesRefreshed = now;
                _classNames = [];
                var tags = manager.Classifier.Classify(fight.Primary);
                foreach (var (key, entry) in fight.MergedCombatants)
                {
                    if (tags.TryGetValue(key, out var tag))
                        _classNames[entry.Combatant.Name] = tag.Class.ClassName;
                }
            }

            var seconds = Math.Max(1.0, fight.Duration.TotalSeconds);
            var allyKeys = fight.MergedAllyKeys;
            List<(string Name, long Total)> totals = [];
            foreach (var (key, entry) in fight.MergedCombatants)
            {
                if (!allyKeys.Contains(key))
                    continue;
                var combatant = entry.Combatant;
                var total = metric switch
                {
                    "HPS" => combatant.Healed,
                    "Tanking" => combatant.DamageTaken,
                    _ => combatant.Damage,
                };
                if (total > 0)
                    totals.Add((combatant.Name, total));
            }
            totals.Sort((a, b) => b.Total.CompareTo(a.Total));

            var top = totals.Count > 0 ? totals[0].Total : 1;
            List<MiniParseRow> rows = [];
            for (var i = 0; i < totals.Count && i < maxRows; i++)
            {
                var (name, total) = totals[i];
                rows.Add(new MiniParseRow(
                    i + 1, name, _classNames.GetValueOrDefault(name),
                    total / seconds, (double)total / top, total));
            }

            return new MiniParseData(
                fight.Title,
                fight.Duration.ToString(@"m\:ss"),
                metric,
                rows);
        }
    }
}
