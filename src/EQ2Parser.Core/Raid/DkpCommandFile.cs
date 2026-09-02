namespace EQ2Parser.Core.Raid;

/// <summary>
/// Pure builders for the EQ2 command files this app writes into the game's
/// install dir, executed in-game via a macro bound to
/// "/do_file_commands &lt;file&gt;" — one macro per file. Two flavours:
///
///  - Refresh: runs "/who all raid" then "/who all guild" — the exact
///    ordered pair <see cref="RaidRosterTracker"/> classifies positionally.
///  - DKP award: "/guild points add …" for the whole raid plus one line per
///    confirmed sit-out. Points commands produce NO log feedback (verified
///    against 1.9 GB of real logs) — awards are fire-and-forget in-game.
/// </summary>
public static class DkpCommandFile
{
    public const string RefreshFileName = "eq2lexicon-raid-list.txt";
    public const string AwardFileName = "eq2lexicon-raid-dkp.txt";

    /// <summary>The roster-refresh command pair (raid first, guild second —
    /// order is the classification contract).</summary>
    public static string BuildRefresh() =>
        "/who all raid\r\n/who all guild\r\n";

    /// <summary>The DKP award file: one raid-wide grant plus one line per
    /// sit-out. Reasons are sanitised to a single line.</summary>
    public static string BuildAward(int points, string reason, IReadOnlyList<string> sitOutNames)
    {
        var clean = SanitizeReason(reason);
        var lines = new List<string> { $"/guild points add {points} raid {clean}" };
        lines.AddRange(sitOutNames
            .Where(Combat.Swing.LooksLikePlayer)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => $"/guild points add {points} {n} {clean}"));
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>Collapse a free-text reason onto one safe line (newlines
    /// would split into stray commands; leading slashes would become them).</summary>
    public static string SanitizeReason(string reason)
    {
        var flat = string.Join(' ', reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimStart('/');
        return flat.Length == 0 ? "Raid DKP" : flat.Length > 120 ? flat[..120] : flat;
    }
}
