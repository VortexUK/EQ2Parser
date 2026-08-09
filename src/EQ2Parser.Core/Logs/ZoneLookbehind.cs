using System.IO;
using System.Text;

namespace EQ2Parser.Core.Logs;

/// <summary>
/// Attach-time zone recovery: a source that starts tailing mid-file (the
/// normal case — the app is launched while the user is already sitting in
/// a zone) has the "You have entered …" line BEHIND its start offset, so
/// <c>ParserEngine.CurrentZone</c> stays empty until the next zoning and
/// zone-scoped spell timers can't disambiguate. This scans a bounded
/// window backwards from the start offset for the newest zone line.
/// Can't help when logging was off during the zone-in — the line simply
/// isn't in the file — but covers the common launch-mid-session case.
/// </summary>
public static class ZoneLookbehind
{
    /// <summary>How far behind the start offset to look. Two megabytes is
    /// hours of typical logging — a zone line older than that is stale
    /// enough that guessing wrong is a real risk, so stop there.</summary>
    public const int MaxWindowBytes = 2 * 1024 * 1024;

    /// <summary>The newest "You have entered …" zone within the window
    /// ending at <paramref name="beforeOffset"/>, or null when none is
    /// found (or the file can't be read — callers treat both as "still
    /// unknown", never as an error). Pass the SAME encoding the source's
    /// tail reader uses (<see cref="LogTailOptions.Encoding"/>) — decoding
    /// the look-behind differently from the live tail can split combatant
    /// identities on accented names.</summary>
    /// <summary>What the look-behind recovered: the zone, plus the
    /// instance lockout expiry when the "This instance will expire in …"
    /// line followed the zone-in inside the window.</summary>
    public sealed record ZoneSeed(string Zone, DateTimeOffset? InstanceExpiry);

    public static string? FindLastZone(
        string path, long beforeOffset, int maxWindowBytes = MaxWindowBytes, Encoding? encoding = null) =>
        FindLastZoneSeed(path, beforeOffset, maxWindowBytes, encoding)?.Zone;

    public static ZoneSeed? FindLastZoneSeed(
        string path, long beforeOffset, int maxWindowBytes = MaxWindowBytes, Encoding? encoding = null)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var end = Math.Min(beforeOffset, stream.Length);
            if (end <= 0)
                return null;
            var start = Math.Max(0, end - maxWindowBytes);
            var buffer = new byte[end - start];
            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(buffer);

            // Same lenient decode as the tail reader — a torn multi-byte
            // char becomes U+FFFD instead of an exception.
            var text = (encoding
                ?? new UTF8Encoding(false, throwOnInvalidBytes: false)).GetString(buffer);
            var lines = text.Split('\n');
            // The first entry is partial unless the window starts at the
            // real beginning of the file.
            var first = start == 0 ? 0 : 1;
            for (var i = lines.Length - 1; i >= first; i--)
            {
                var raw = lines[i].TrimEnd('\r');
                if (!LogLine.TryParse(raw, out var line))
                    continue;
                if (Grammar.EnglishGrammar.TryParseZoneEntered(line.Message) is { } zone)
                {
                    // The lockout line lands on the zone-in's own second —
                    // check the next couple of complete lines for it.
                    DateTimeOffset? expiry = null;
                    for (var j = i + 1; j < Math.Min(i + 4, lines.Length); j++)
                    {
                        if (!LogLine.TryParse(lines[j].TrimEnd('\r'), out var next))
                            continue;
                        if (Grammar.EnglishGrammar.TryParseInstanceLockout(next.Message) is { } remaining)
                        {
                            expiry = next.Timestamp + remaining;
                            break;
                        }
                    }
                    return new ZoneSeed(zone, expiry);
                }
            }
            return null;
        }
        catch (Exception)
        {
            // Sharing violation / rotation race / permissions — the seed is
            // best-effort; the live parse recovers on the next zone line.
            return null;
        }
    }
}
