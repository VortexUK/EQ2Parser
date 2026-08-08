using System.Reflection;
using System.Text.Json;

namespace EQ2Parser.Core.Combat;

/// <summary>
/// Curated "this fight ends by script, not by a death" markers. Some bosses
/// (ToNT's Palace Overseer, Queen Lenya Thex) never die — a specific NPC
/// say line is the win. When one of these lines appears during an active
/// encounter, the encounter is flagged <see cref="Encounter.ScriptedWin"/>
/// and reports Win regardless of the death-based heuristic.
///
/// The wire shape being matched (real log line):
/// <c>\aNPC 65832 Palace Overseer:Palace Overseer\/a says, "This cannot be!!"</c>
/// — a rule matches when the line is an NPC say, the speaker (the name
/// between ':' and '\/a') is one of the rule's speakers, and the quoted
/// text contains the rule's phrase. Rules live in the embedded
/// scripted_wins.json; curation is release-time (per-boss, evidence from
/// real logs).
/// </summary>
public sealed class ScriptedWins
{
    private sealed record RuleFile(List<RuleEntry>? Rules);

    private sealed record RuleEntry(string[]? Speakers, string? Phrase, string? Note);

    private sealed record Rule(string[] Speakers, string Phrase);

    private readonly List<Rule> _rules = [];

    /// <summary>The embedded curated set, loaded once.</summary>
    public static ScriptedWins Default { get; } = LoadEmbedded();

    public static ScriptedWins Empty { get; } = new();

    public static ScriptedWins LoadEmbedded()
    {
        var wins = new ScriptedWins();
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("EQ2Parser.Core.Resources.scripted_wins.json");
            if (stream is null)
                return wins;
            using var reader = new StreamReader(stream);
            var file = JsonSerializer.Deserialize<RuleFile>(
                reader.ReadToEnd(), JsonDefaults.CaseInsensitive);
            foreach (var entry in file?.Rules ?? [])
            {
                if (entry.Speakers is { Length: > 0 } speakers && !string.IsNullOrEmpty(entry.Phrase))
                    wins._rules.Add(new Rule(speakers, entry.Phrase));
            }
        }
        catch (Exception)
        {
            // A malformed curated file degrades to "no scripted wins", never
            // a parse-path crash.
        }
        return wins;
    }

    /// <summary>True when <paramref name="message"/> is one of the curated
    /// scripted-win say lines. Cheap early-outs — this runs per log line
    /// while a fight is active.</summary>
    public bool TryMatch(string message)
    {
        if (_rules.Count == 0
            || message.Length > 600
            || !message.StartsWith(@"\aNPC ", StringComparison.Ordinal))
            return false;
        var says = message.IndexOf(@"\/a says", StringComparison.Ordinal);
        if (says < 0)
            return false;
        foreach (var rule in _rules)
        {
            if (message.IndexOf(rule.Phrase, says, StringComparison.Ordinal) < 0)
                continue;
            foreach (var speaker in rule.Speakers)
            {
                if (message.Contains($":{speaker}\\/a says", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }
}
