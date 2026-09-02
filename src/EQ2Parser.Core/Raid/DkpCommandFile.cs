namespace EQ2Parser.Core.Raid;

/// <summary>
/// Pure builders for the EQ2 command files this app writes into the game's
/// install dir, executed in-game via a macro bound to
/// "/do_file_commands &lt;file&gt;" — one macro per file. Lines in the file
/// are BARE commands (no leading slash — do_file_commands adds it). Two
/// flavours:
///
///  - Refresh: runs "whoraid" (the raid-who shortcut) then "who all guild"
///    — the exact ordered raid-then-guild pair
///    <see cref="RaidRosterTracker"/> classifies positionally.
///  - DKP award: "guild points add …" lines. With a mains map (fetched from
///    EQ2Lexicon), every raid member gets an INDIVIDUAL grant addressed to
///    their raid MAIN — a player on a raid alt still banks DKP on the main.
///    Without the map (site unreachable, no roster set up) it falls back to
///    the bulk "raid" grant, which credits whichever character is in raid.
///
/// The game throttles points commands — ONE succeeds per macro press, the
/// rest log "You must wait before sending another guild points command."
/// (verified live 2026-09-02; there is no in-file delay — "delay" is an
/// unknown command). Successes are silent. So every award file ends with
/// <see cref="MarkerCommand"/>, a deliberately-unknown command that always
/// logs: each press yields K throttle lines + the marker, telling the app
/// exactly how many awards remain. The app pops the applied command(s),
/// rewrites the file, and the officer just presses the macro until done.
/// </summary>
public static class DkpCommandFile
{
    public const string RefreshFileName = "eq2lexicon-raid-list.txt";
    public const string AwardFileName = "eq2lexicon-raid-dkp.txt";

    /// <summary>Last line of every award file. Unknown to the game, immune
    /// to the points throttle — its "Unknown command" log line is the
    /// press-completed signal.</summary>
    public const string MarkerCommand = "eq2lexicon_dkp_done";

    /// <summary>The marker's exact log echo (the game quotes the whole
    /// line, e.g. "Unknown command: 'delay 1'").</summary>
    public const string MarkerLogLine = "Unknown command: 'eq2lexicon_dkp_done'";

    /// <summary>The throttle failure's exact log line — one per award
    /// command that did NOT run this press.</summary>
    public const string ThrottleLogLine = "You must wait before sending another guild points command.";

    /// <summary>The roster-refresh command pair (raid first, guild second —
    /// order is the classification contract). "whoraid" is the in-game
    /// raid-who shortcut; guild has no such shortcut.</summary>
    public static string BuildRefresh() =>
        "whoraid\r\nwho all guild\r\n";

    /// <summary>The award COMMANDS (no marker — see BuildQueueFile).
    /// <paramref name="mains"/> maps character → raid main (best effort,
    /// from the site's roster + claims); null or empty means unknown →
    /// bulk raid grant. Reasons are sanitised to a single line. A main
    /// whose alt AND main are both present (dual-box) is awarded once; a
    /// sit-out whose main was already granted via the raid list is
    /// skipped too.</summary>
    public static List<string> BuildAwardCommands(
        int points,
        string reason,
        IReadOnlyList<string> raidNames,
        IReadOnlyList<string> sitOutNames,
        IReadOnlyDictionary<string, string>? mains = null)
    {
        var clean = SanitizeReason(reason);
        var lines = new List<string>();
        var awarded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string ToMain(string name) =>
            mains is not null && mains.TryGetValue(name, out var main) && !string.IsNullOrWhiteSpace(main)
                ? main
                : name;

        if (mains is null || mains.Count == 0)
        {
            lines.Add($"guild points add {points} raid {clean}");
        }
        else
        {
            foreach (var main in raidNames
                .Where(Combat.Swing.LooksLikePlayer)
                .Select(ToMain)
                .Where(awarded.Add)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList())
            {
                lines.Add($"guild points add {points} {main} {clean}");
            }
        }

        lines.AddRange(sitOutNames
            .Where(Combat.Swing.LooksLikePlayer)
            .Select(ToMain)
            .Where(awarded.Add)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => $"guild points add {points} {n} {clean}"));
        return lines;
    }

    /// <summary>The file text for the remaining queue: the commands plus
    /// the trailing marker. An empty queue writes a marker-only file, so a
    /// stray extra macro press can't re-award anything.</summary>
    public static string BuildQueueFile(IReadOnlyList<string> commands) =>
        string.Join("\r\n", commands.Append(MarkerCommand)) + "\r\n";

    /// <summary>Convenience: the full initial award file.</summary>
    public static string BuildAward(
        int points,
        string reason,
        IReadOnlyList<string> raidNames,
        IReadOnlyList<string> sitOutNames,
        IReadOnlyDictionary<string, string>? mains = null) =>
        BuildQueueFile(BuildAwardCommands(points, reason, raidNames, sitOutNames, mains));

    /// <summary>Queue math for one detected press: <paramref name="failures"/>
    /// throttle lines were logged, so that many commands remain. Returns the
    /// remaining queue and how many were applied this press (0 when the
    /// whole press was throttled — e.g. pressed again too quickly).</summary>
    public static (List<string> Remaining, int Applied) AdvanceQueue(IReadOnlyList<string> queue, int failures)
    {
        var applied = Math.Max(0, queue.Count - Math.Max(0, failures));
        return ([.. queue.Skip(applied)], applied);
    }

    /// <summary>Collapse a free-text reason onto one safe line (a newline
    /// would split into stray commands).</summary>
    public static string SanitizeReason(string reason)
    {
        var flat = string.Join(' ', reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimStart('/');
        return flat.Length == 0 ? "Raid DKP" : flat.Length > 120 ? flat[..120] : flat;
    }
}
