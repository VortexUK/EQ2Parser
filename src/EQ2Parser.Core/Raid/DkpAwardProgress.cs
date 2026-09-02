namespace EQ2Parser.Core.Raid;

/// <summary>
/// Detects DKP-macro presses from the log. The award file's commands run in
/// order: the throttle fails all-but-one with
/// <see cref="DkpCommandFile.ThrottleLogLine"/> (one line each), then the
/// trailing <see cref="DkpCommandFile.MarkerCommand"/> logs
/// <see cref="DkpCommandFile.MarkerLogLine"/>. The marker is therefore the
/// end-of-press signal, and the throttle-line count since the previous
/// marker is exactly how many award commands remain.
///
/// Fed from the same live-only RaidLine hook as the roster tracker; lines
/// arrive on the pump thread — handlers must stay cheap.
/// </summary>
public sealed class DkpAwardProgress
{
    private readonly object _gate = new();
    private int _failures;

    /// <summary>One macro press completed; the argument is the number of
    /// throttle-failure lines observed = award commands still queued.
    /// Raised on the pump thread.</summary>
    public event Action<int>? PressDetected;

    /// <summary>Prefilter shapes for the pump-thread hook.</summary>
    public static bool LooksRelevant(string message) =>
        message.StartsWith("You must wait before sending another guild points", StringComparison.Ordinal)
        || message.StartsWith("Unknown command: 'eq2lexicon", StringComparison.Ordinal);

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
        if (message != DkpCommandFile.MarkerLogLine)
            return;
        int failures;
        lock (_gate)
        {
            failures = _failures;
            _failures = 0;
        }
        PressDetected?.Invoke(failures);
    }
}
