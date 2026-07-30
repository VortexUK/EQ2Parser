using System.Text.RegularExpressions;
using EQ2Parser.Core.Grammar;
using EQ2Parser.Core.Logs;
using Xunit.Abstractions;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// Grammar-gap analysis: run the real grammar over a REAL log
/// (EQ2PARSER_SAMPLE_LOG) and cluster the lines it did NOT match into
/// normalized templates, ranked by frequency. Chat/social noise is excluded
/// so the report surfaces actual combat grammar gaps. This is the tool that
/// drives each grammar coverage pass.
/// </summary>
public partial class CoverageReportTests(ITestOutputHelper output)
{
    [GeneratedRegex(@"\b\d[\d,]*\b")]
    private static partial Regex Numbers();

    [GeneratedRegex(@"\\a[A-Z]+ -?\d+ [^\\]+\\/a")]
    private static partial Regex GameLinks();

    // Social/system noise we never intend to parse.
    [GeneratedRegex("\"|tells |says?,| say | shouts?,|Guild:|auction|You have (gained|earned|received)|experience|Logging to|MOTD|has come online|has gone (offline|linkdead)|is now (AFK|\\*ON\\*|\\*OFF\\*)")]
    private static partial Regex Noise();

    [Fact]
    public void Report_Unmatched_Line_Templates()
    {
        var path = Environment.GetEnvironmentVariable("EQ2PARSER_SAMPLE_LOG");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            output.WriteLine("EQ2PARSER_SAMPLE_LOG not set — skipped.");
            return;
        }

        long total = 0, matched = 0, noise = 0;
        var templates = new Dictionary<string, (long Count, string Example)>(StringComparer.Ordinal);

        foreach (var raw in File.ReadLines(path))
        {
            if (!LogLine.TryParse(raw, out var line))
                continue;
            total++;
            if (EnglishGrammar.TryParse(line.Message) is not null)
            {
                matched++;
                continue;
            }
            if (Noise().IsMatch(line.Message))
            {
                noise++;
                continue;
            }

            var template = Numbers().Replace(GameLinks().Replace(line.Message, "<link>"), "N");
            // Collapse to the first 10 words so name variance clusters together.
            var words = template.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            template = string.Join(' ', words.Take(10));
            if (templates.TryGetValue(template, out var t))
                templates[template] = (t.Count + 1, t.Example);
            else
                templates[template] = (1, line.Message.Length > 160 ? line.Message[..160] : line.Message);
        }

        output.WriteLine($"lines: {total:N0} | matched: {matched:N0} ({100.0 * matched / Math.Max(1, total):F1}%) | noise: {noise:N0} ({100.0 * noise / Math.Max(1, total):F1}%)");
        output.WriteLine($"unmatched non-noise: {total - matched - noise:N0} ({100.0 * (total - matched - noise) / Math.Max(1, total):F1}%)");
        output.WriteLine("--- top unmatched templates ---");
        foreach (var (template, (count, example)) in templates.OrderByDescending(kv => kv.Value.Count).Take(40))
        {
            output.WriteLine($"{count,8:N0}  {template}");
            output.WriteLine($"          e.g. {example}");
        }
    }
}
