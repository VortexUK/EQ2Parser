namespace EQ2Parser.Core.Raid;

/// <summary>One purchased raid drop to charge: the buyer pays ``Cost`` DKP.
/// The ledger comment is the raw in-game item LINK (<c>\aITEM …\/a</c>, as
/// logged) so the guild-event entry is clickable; the plain name is the
/// fallback when no link was captured.</summary>
public sealed record LootCharge(string Buyer, int Cost, string ItemName, string? ItemLink = null);

/// <summary>One resolved DKP award: ``Player`` is already mains-resolved
/// (or the literal "raid" bulk grant); ``Reason`` is sanitised.</summary>
public sealed record AwardEntry(string Player, int Points, string Reason);

/// <summary>
/// Pure builders for the EQ2 command files this app writes into the game's
/// install dir, executed in-game via a macro bound to
/// "/do_file_commands &lt;file&gt;" — one macro per file. Lines in the file
/// are BARE commands (no leading slash — do_file_commands adds it).
///
///  - Refresh: runs "whoraid" (the raid-who shortcut) then "who all guild"
///    — the exact ordered raid-then-guild pair
///    <see cref="RaidRosterTracker"/> classifies positionally.
///  - Award / loot: ONE "guild points add …" step at a time. The game
///    throttles points commands (one per macro press, the rest log
///    <see cref="ThrottleLogLine"/>; successes are silent — verified live
///    2026-09-02), so each file carries a single step: the points command,
///    an officer-chat announcement of exactly that step, and a trailing
///    marker. The announcement's log echo is the app's positive,
///    content-addressed confirmation — on seeing it (with no throttle
///    failure in the same press) the app ticks that item/award off and
///    rewrites the file with the NEXT step. The officer just presses the
///    macro until the status says done, and the whole raid's officers see
///    the attribution in chat as it happens.
///
/// The marker is a deliberately-unknown command whose "Unknown command"
/// echo still identifies which macro was pressed (award vs loot) — it is
/// the "pressed on an empty file" signal once everything has applied.
/// </summary>
public static class DkpCommandFile
{
    public const string RefreshFileName = "eq2lexicon-raid-list.txt";
    public const string AwardFileName = "eq2lexicon-raid-dkp.txt";
    public const string LootFileName = "eq2lexicon-raid-loot.txt";

    /// <summary>Officer-chat command (bare — do_file_commands adds the
    /// slash). The announcement doubles as the confirmation signal.</summary>
    public const string AnnounceCommand = "of";

    /// <summary>Last line of every award/loot file. Unknown to the game,
    /// immune to the points throttle — its "Unknown command" log line says
    /// which macro was pressed.</summary>
    public const string MarkerCommand = "eq2lexicon_dkp_done";
    public const string LootMarkerCommand = "eq2lexicon_loot_done";

    /// <summary>The markers' exact log echoes (the game quotes the whole
    /// line, e.g. "Unknown command: 'delay 1'").</summary>
    public const string MarkerLogLine = "Unknown command: 'eq2lexicon_dkp_done'";
    public const string LootMarkerLogLine = "Unknown command: 'eq2lexicon_loot_done'";

    /// <summary>The throttle failure's exact log line — logged when the
    /// points command did NOT run this press (pressed again too soon).</summary>
    public const string ThrottleLogLine = "You must wait before sending another guild points command.";

    /// <summary>Distinctive substrings of the two announcement shapes —
    /// the cheap log-line detectors (the wrapper differs between your own
    /// echo and other officers', so match on the payload).</summary>
    public const string AwardEchoNeedle = " dkp awarded to ";
    public const string LootEchoNeedle = " assigned to ";

    /// <summary>The roster-refresh command pair (raid first, guild second —
    /// order is the classification contract). "whoraid" is the in-game
    /// raid-who shortcut; guild has no such shortcut.</summary>
    public static string BuildRefresh() =>
        "whoraid\r\nwho all guild\r\n";

    /// <summary>The resolved award list (no commands yet — see AwardCommand).
    /// <paramref name="mains"/> maps character → raid main (best effort,
    /// from the site's roster + claims); null or empty means unknown →
    /// bulk raid grant. A main whose alt AND main are both present
    /// (dual-box) is awarded once; a sit-out whose main was already granted
    /// via the raid list is skipped too.</summary>
    public static List<AwardEntry> BuildAwardEntries(
        int points,
        string reason,
        IReadOnlyList<string> raidNames,
        IReadOnlyList<string> sitOutNames,
        IReadOnlyDictionary<string, string>? mains = null)
    {
        var clean = SanitizeReason(reason);
        var entries = new List<AwardEntry>();
        var awarded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string ToMain(string name) =>
            mains is not null && mains.TryGetValue(name, out var main) && !string.IsNullOrWhiteSpace(main)
                ? main
                : name;

        if (mains is null || mains.Count == 0)
        {
            entries.Add(new AwardEntry("raid", points, clean));
        }
        else
        {
            entries.AddRange(raidNames
                .Where(Combat.Swing.LooksLikePlayer)
                .Select(ToMain)
                .Where(awarded.Add)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(main => new AwardEntry(main, points, clean)));
        }

        entries.AddRange(sitOutNames
            .Where(Combat.Swing.LooksLikePlayer)
            .Select(ToMain)
            .Where(awarded.Add)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new AwardEntry(n, points, clean)));
        return entries;
    }

    public static string AwardCommand(AwardEntry entry) =>
        $"guild points add {entry.Points} {entry.Player} {entry.Reason}";

    /// <summary>The officer-chat payload for one award — echoed back in the
    /// log, where it confirms exactly this grant applied. Contains
    /// <see cref="AwardEchoNeedle"/> by construction.</summary>
    public static string AwardAnnouncement(AwardEntry entry) =>
        $"{entry.Points} dkp awarded to {(entry.Player == "raid" ? "the raid" : entry.Player)} ({entry.Reason})";

    /// <summary>One loot deduction: cost from the buyer's MAIN, the item
    /// link as the ledger comment.</summary>
    public static string LootCommand(LootCharge charge, IReadOnlyDictionary<string, string>? mains = null)
    {
        var target =
            mains is not null && mains.TryGetValue(charge.Buyer, out var main) && !string.IsNullOrWhiteSpace(main)
                ? main
                : charge.Buyer;
        return $"guild points add -{charge.Cost} {target} {LootComment(charge)}";
    }

    /// <summary>The officer-chat payload for one loot charge — names the
    /// character who received the item (the deduction itself may target
    /// their main). Contains <see cref="LootEchoNeedle"/> by construction.</summary>
    public static string LootAnnouncement(LootCharge charge) =>
        $"{LootComment(charge)} assigned to {charge.Buyer} for {charge.Cost} dkp";

    /// <summary>The ledger comment: the raw item link when it survives
    /// sanitisation INTACT (a length-capped truncation would leave broken
    /// <c>\aITEM</c> markup in the guild event), else the plain name.</summary>
    private static string LootComment(LootCharge charge)
    {
        if (charge.ItemLink is { Length: > 0 } link)
        {
            var flat = SanitizeReason(link);
            if (flat.EndsWith(@"\/a", StringComparison.Ordinal))
                return flat;
        }
        return SanitizeReason(charge.ItemName);
    }

    /// <summary>The file text for one step: the points command, its
    /// officer-chat announcement, and the trailing marker. A null command
    /// writes a marker-only file (nothing pending — a stray extra macro
    /// press can't re-apply anything).</summary>
    public static string BuildStepFile(string? command, string? announcement, string marker) =>
        command is null
            ? BuildQueueFile([], marker)
            : BuildQueueFile([command, $"{AnnounceCommand} {announcement}"], marker);

    /// <summary>Raw file assembly: the lines plus the trailing marker.</summary>
    public static string BuildQueueFile(IReadOnlyList<string> commands, string marker = MarkerCommand) =>
        string.Join("\r\n", commands.Append(marker)) + "\r\n";

    /// <summary>Collapse a free-text reason onto one safe line (a newline
    /// would split into stray commands).</summary>
    public static string SanitizeReason(string reason)
    {
        var flat = string.Join(' ', reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimStart('/');
        return flat.Length == 0 ? "Raid DKP" : flat.Length > 120 ? flat[..120] : flat;
    }
}
