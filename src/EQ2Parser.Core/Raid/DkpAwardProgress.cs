namespace EQ2Parser.Core.Raid;

/// <summary>
/// Detects guild-points activity from the log. Each award/loot file carries
/// ONE points command, its officer-chat announcement, and a trailing marker
/// (see <see cref="DkpCommandFile"/>). A press therefore logs, in order:
///
///  - <see cref="DkpCommandFile.ThrottleLogLine"/> IF the points command was
///    throttled (pressed too soon after another points command);
///  - the announcement echo — your own as `You say to the officers, "…"`,
///    other officers' as `\aPC …\/a says to the officers, "…"`. Matched by
///    payload needle, wrapper-agnostic: this is the positive confirmation
///    that THIS award/charge ran (when no throttle failure preceded it);
///  - the marker's "Unknown command" echo — identifies which macro was
///    pressed; with nothing pending it is the "all done" signal.
///
/// Fed from the same live-only RaidLine hook as the roster tracker; lines
/// arrive on the pump thread — handlers must stay cheap.
/// </summary>
public sealed class DkpAwardProgress
{
    private readonly object _gate = new();
    private int _failures;

    /// <summary>A macro press's marker line. Arguments: the marker command
    /// (award vs loot file) and the throttle-failure count since the last
    /// signal. Raised on the pump thread.</summary>
    public event Action<string, int>? PressDetected;

    /// <summary>An award announcement echo ("N dkp awarded to X (reason)").
    /// Arguments: the full log line (match your pending payloads against
    /// it) and the throttle-failure count — non-zero means the points
    /// command did NOT run this press, only the chat line did.</summary>
    public event Action<string, int>? AwardEchoSeen;

    /// <summary>A loot announcement echo ("&lt;link&gt; assigned to X for
    /// N dkp"). Same argument contract as <see cref="AwardEchoSeen"/>.</summary>
    public event Action<string, int>? LootEchoSeen;

    /// <summary>Prefilter shapes for the pump-thread hook.</summary>
    public static bool LooksRelevant(string message) =>
        message.StartsWith("You must wait before sending another guild points", StringComparison.Ordinal)
        || message.StartsWith("Unknown command: 'eq2lexicon", StringComparison.Ordinal)
        || message.Contains(DkpCommandFile.AwardEchoNeedle, StringComparison.Ordinal)
        || (message.Contains(DkpCommandFile.LootEchoNeedle, StringComparison.Ordinal)
            && message.Contains(@"\aITEM", StringComparison.Ordinal)
            && message.Contains(" dkp", StringComparison.Ordinal));

    /// <summary>Feed one LIVE log line (signature matches the RaidLine hook).</summary>
    public void OnLine(string message, DateTimeOffset time)
    {
        _ = time;
        if (message == DkpCommandFile.ThrottleLogLine)
        {
            lock (_gate)
                _failures++;
            return;
        }

        if (message.Contains(DkpCommandFile.AwardEchoNeedle, StringComparison.Ordinal))
        {
            AwardEchoSeen?.Invoke(message, TakeFailures());
            return;
        }
        if (message.Contains(DkpCommandFile.LootEchoNeedle, StringComparison.Ordinal)
            && message.Contains(@"\aITEM", StringComparison.Ordinal)
            && message.Contains(" dkp", StringComparison.Ordinal))
        {
            LootEchoSeen?.Invoke(message, TakeFailures());
            return;
        }

        var marker = message switch
        {
            DkpCommandFile.MarkerLogLine => DkpCommandFile.MarkerCommand,
            DkpCommandFile.LootMarkerLogLine => DkpCommandFile.LootMarkerCommand,
            _ => null,
        };
        if (marker is not null)
            PressDetected?.Invoke(marker, TakeFailures());
    }

    private int TakeFailures()
    {
        lock (_gate)
        {
            var failures = _failures;
            _failures = 0;
            return failures;
        }
    }
}
