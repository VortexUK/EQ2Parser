using EQ2Parser.Core.Combat;

namespace EQ2Parser.Core.Analysis;

/// <summary>One row of the mini parse: rank, name, metric value, share of
/// the top row, deaths, and the detected class (for colouring).</summary>
public sealed record MiniParseRow(
    int Rank, string Name, string? ClassName, double Value, double Fraction, long Total, int Deaths);

/// <summary>Header + rows for the mini parse window. RaidValue is the
/// whole raid's metric per second — every ally, not just visible rows.</summary>
public sealed record MiniParseData(
    string Title, string DurationLabel, string MetricLabel, double RaidValue, IReadOnlyList<MiniParseRow> Rows);

/// <summary>
/// The mini parse maths: ally totals for the chosen metric, sorted, capped
/// to the visible rows, each carrying its share of the TOP row (the bar
/// fill) and its per-second rate over the fight duration (clamped to ≥1s —
/// log stamps are whole seconds). Pure — the App side picks the fight and
/// caches class detection, then hands the data here.
/// </summary>
public static class MiniParseBuilder
{
    public static MiniParseData Build(
        string title, TimeSpan duration, string metric, int maxRows,
        IEnumerable<(string Key, Combatant Combatant)> members,
        IReadOnlySet<string> allyKeys,
        IReadOnlyDictionary<string, string?> classNames)
    {
        var seconds = Math.Max(1.0, duration.TotalSeconds);
        List<(string Name, long Total, int Deaths)> totals = [];
        foreach (var (key, combatant) in members)
        {
            if (!allyKeys.Contains(key))
                continue;
            var total = metric switch
            {
                "HPS" => combatant.Healed,
                "Tanking" => combatant.DamageTaken,
                _ => combatant.Damage,
            };
            if (total > 0)
                totals.Add((combatant.Name, total, combatant.Deaths));
        }
        totals.Sort((a, b) => b.Total.CompareTo(a.Total));

        long raidTotal = 0;
        foreach (var (_, total, _) in totals)
            raidTotal += total;
        var top = totals.Count > 0 ? totals[0].Total : 1;
        List<MiniParseRow> rows = [];
        for (var i = 0; i < totals.Count && i < maxRows; i++)
        {
            var (name, total, deaths) = totals[i];
            rows.Add(new MiniParseRow(
                i + 1, name, classNames.GetValueOrDefault(name),
                total / seconds, (double)total / top, total, deaths));
        }

        return new MiniParseData(
            title,
            duration.ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture),
            metric,
            raidTotal / seconds,
            rows);
    }
}
