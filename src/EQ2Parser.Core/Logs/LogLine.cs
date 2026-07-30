namespace EQ2Parser.Core.Logs;

/// <summary>
/// One parsed EQ2 log line. The on-disk shape is:
/// <c>(1753738000)[Mon Jul 28 22:26:40 2026] You hit a training dummy for 100 points of crushing damage.</c>
/// — a unix epoch in parentheses, the client's local-time string in brackets,
/// then the message. The epoch is authoritative for all timing (the bracketed
/// string is locale/DST-ambiguous and kept only for display).
/// </summary>
public readonly record struct LogLine(long Epoch, string LocalStamp, string Message)
{
    /// <summary>Wall-clock arrival stamp from the tail reader (the app's
    /// second clock — sub-second, live mode only). Null on imports. Never
    /// feeds ACT/site-compatible stat math; see TailedLine.</summary>
    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>
    /// Parse a raw log line. Returns false for anything that doesn't match the
    /// EQ2 shape (corrupt tail reads, BOMs, other games' logs) — callers skip
    /// those rather than throw, because a live tail WILL see partial lines.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> raw, out LogLine line)
    {
        line = default;
        if (raw.Length < 8 || raw[0] != '(')
            return false;

        var closeParen = raw.IndexOf(')');
        if (closeParen < 2 || !long.TryParse(raw[1..closeParen], out var epoch))
            return false;

        var rest = raw[(closeParen + 1)..];
        if (rest.Length < 3 || rest[0] != '[')
            return false;

        var closeBracket = rest.IndexOf(']');
        if (closeBracket < 1)
            return false;

        var stamp = rest[1..closeBracket];
        var message = rest[(closeBracket + 1)..].TrimStart(' ');
        line = new LogLine(epoch, stamp.ToString(), message.ToString());
        return true;
    }

    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeSeconds(Epoch);
}
