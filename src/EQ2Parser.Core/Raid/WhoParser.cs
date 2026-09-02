using System.Text.RegularExpressions;

namespace EQ2Parser.Core.Raid;

/// <summary>One row of /who output. Anonymous players expose only the name
/// (and possibly the AFK suffix) — every other field is null for them.</summary>
public sealed record WhoRow(string Name, int? Level, string? Class, string? Race, string? Guild, string? Zone, bool Afk);

/// <summary>A complete /who result block (header → rows → "N players found").</summary>
public sealed record WhoResult(string? ZoneFilter, IReadOnlyList<WhoRow> Rows, DateTimeOffset CompletedAt);

/// <summary>
/// Stateful assembler for /who output blocks (verbatim live shapes, mined
/// from real logs 2026-09):
///
///   /who search results:                          (or "…results for &lt;Zone&gt;:")
///   ------------------------------------------
///   [70 Conjuror] Tsuna (Freeblood) &lt;Exordium&gt; Zone: Throne of New Tunaria
///   [Anonymous] Betabonk
///   [Anonymous] Ashtar (AFK)
///   24 players found                              (or "1 player found")
///
/// Feed every candidate line with its log timestamp; a completed block is
/// returned from <see cref="Feed"/> when the footer lands. Unrelated lines
/// while a block is open are ignored (chat can interleave); a block is
/// abandoned when a new header arrives, on "There are no players…", after
/// <see cref="MaxRows"/> rows, or when a line arrives more than
/// <see cref="BlockTimeout"/> after the header (who output bursts within a
/// second — anything later is not part of the block).
/// </summary>
public sealed partial class WhoParser
{
    public const int MaxRows = 200;
    public static readonly TimeSpan BlockTimeout = TimeSpan.FromSeconds(5);

    [GeneratedRegex(@"^/who search results(?: for (?<zone>.+?))?:$")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^\[(?<level>\d+) (?<cls>[A-Za-z]+)\] (?<name>[A-Za-z]+) \((?<race>[A-Za-z ']+)\)(?: <(?<guild>[^>]+)>)?(?: Zone: (?<zone>.+?))?(?<afk> \(AFK\))?$")]
    private static partial Regex DetailRowRegex();

    [GeneratedRegex(@"^\[Anonymous\] (?<name>[A-Za-z]+)(?<afk> \(AFK\))?$")]
    private static partial Regex AnonRowRegex();

    [GeneratedRegex(@"^\d+ players? found$")]
    private static partial Regex FooterRegex();

    private string? _zoneFilter;
    private List<WhoRow>? _rows;
    private DateTimeOffset _headerAt;

    /// <summary>Cheap pre-check callers can use before paying for Feed.</summary>
    public static bool LooksRelevant(string message) =>
        message.StartsWith("/who search results", StringComparison.Ordinal)
        || message.StartsWith('[')
        || message.EndsWith("player found", StringComparison.Ordinal)
        || message.EndsWith("players found", StringComparison.Ordinal);

    /// <summary>Feed one log line. Returns a completed block when this line
    /// was its footer, else null.</summary>
    public WhoResult? Feed(string message, DateTimeOffset time)
    {
        var header = HeaderRegex().Match(message);
        if (header.Success)
        {
            _zoneFilter = header.Groups["zone"].Success ? header.Groups["zone"].Value : null;
            _rows = [];
            _headerAt = time;
            return null;
        }
        if (_rows is null)
            return null;
        if (time - _headerAt > BlockTimeout)
        {
            _rows = null; // stale block — this line belongs to something else
            return null;
        }
        if (FooterRegex().IsMatch(message))
        {
            var result = new WhoResult(_zoneFilter, _rows, time);
            _rows = null;
            return result;
        }
        var detail = DetailRowRegex().Match(message);
        if (detail.Success)
        {
            _rows.Add(new WhoRow(
                detail.Groups["name"].Value,
                int.Parse(detail.Groups["level"].Value, System.Globalization.CultureInfo.InvariantCulture),
                detail.Groups["cls"].Value,
                detail.Groups["race"].Value,
                detail.Groups["guild"].Success ? detail.Groups["guild"].Value : null,
                detail.Groups["zone"].Success ? detail.Groups["zone"].Value : null,
                detail.Groups["afk"].Success));
        }
        else
        {
            var anon = AnonRowRegex().Match(message);
            if (anon.Success)
                _rows.Add(new WhoRow(anon.Groups["name"].Value, null, null, null, null, null, anon.Groups["afk"].Success));
            // else: unrelated interleaved line — ignore, block stays open
        }
        if (_rows.Count > MaxRows)
            _rows = null; // runaway — abandon
        return null;
    }
}
