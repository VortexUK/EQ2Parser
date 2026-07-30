using System.Runtime.CompilerServices;
using System.Text;

namespace EQ2Parser.Core.Logs;

/// <summary>Options for <see cref="LogTailReader"/>.</summary>
public sealed record LogTailOptions
{
    /// <summary>Start at the end of the file (live mode) instead of the beginning (import mode).</summary>
    public bool StartAtEnd { get; init; } = true;

    /// <summary>How often to poll for new content. Polling beats FileSystemWatcher
    /// here: watchers are notoriously unreliable for files a game keeps open and
    /// appends to without touching metadata. 10 ms matches ACT's reader cadence —
    /// the poll interval is effectively trigger-alert latency, and a length
    /// check is a near-free syscall.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Text encoding of the log. EQ2 logs are UTF-8 on modern clients;
    /// the decoder never throws (invalid sequences become U+FFFD) so a wrong
    /// guess degrades display, not parsing.</summary>
    public Encoding Encoding { get; init; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Wall-clock source for arrival stamps (injectable for tests).</summary>
    public Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.UtcNow;
}

/// <summary>A raw line plus the wall-clock moment its read batch was observed.
/// The arrival stamp is the app's SECOND clock: log timestamps (whole-second)
/// stay authoritative for all ACT/site-compatible stats; arrival time powers
/// sub-second concerns — timer anchoring, live-DPS smoothness, replay spacing,
/// cross-log alignment. Lines flushed together share one stamp (that is the
/// physical truth of buffered log writes, not a limitation to hide).</summary>
public readonly record struct TailedLine(string Raw, DateTimeOffset ObservedAt);

/// <summary>
/// Tails an EQ2 log file, yielding complete lines as they are written.
///
/// Behaviour contract (mirrors what 20 years of ACT taught the ecosystem):
///   * survives the file not existing yet (waits for it),
///   * detects truncation/rotation (file shrank → restart from 0),
///   * never yields a partial line — carry-over happens at the BYTE level so a
///     multi-byte character split across two writes is never corrupted,
///   * tolerates the game holding the file open (FileShare.ReadWrite | Delete).
/// </summary>
public sealed class LogTailReader(string path, LogTailOptions? options = null)
{
    private readonly LogTailOptions _options = options ?? new LogTailOptions();

    /// <summary>Raw complete lines (no trailing CR/LF) with arrival stamps,
    /// forever until cancelled.</summary>
    public async IAsyncEnumerable<TailedLine> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        long position = -1; // -1 = not yet initialised (resolve on first sight of the file)
        var carry = Array.Empty<byte>(); // partial-line bytes from the previous read

        while (!ct.IsCancellationRequested)
        {
            if (File.Exists(path))
            {
                FileStream? stream = null;
                try
                {
                    stream = new FileStream(
                        path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                }
                catch (IOException)
                {
                    // Transient sharing violation (e.g. AV scan); retry next poll.
                }

                if (stream is not null)
                {
                    using (stream)
                    {
                        if (position < 0)
                            position = _options.StartAtEnd ? stream.Length : 0;
                        if (stream.Length < position)
                        {
                            // Truncated or rotated: start over, drop any carry.
                            position = 0;
                            carry = [];
                        }

                        if (stream.Length > position)
                        {
                            stream.Position = position;
                            var fresh = new byte[stream.Length - position];
                            var read = await stream.ReadAsync(fresh.AsMemory(), ct).ConfigureAwait(false);
                            position += read;
                            // One arrival stamp per read batch: lines flushed
                            // together genuinely arrived together.
                            var observedAt = _options.Clock();

                            var buffer = Concat(carry, fresh.AsSpan(0, read));
                            var consumed = 0;
                            foreach (var (start, length) in CompleteLines(buffer))
                            {
                                consumed = start + length;
                                yield return new TailedLine(DecodeLine(buffer.AsSpan(start, length)), observedAt);
                            }
                            // Skip the newline byte(s) belonging to the last line.
                            while (consumed < buffer.Length && (buffer[consumed] == (byte)'\n' || buffer[consumed] == (byte)'\r'))
                                consumed++;
                            carry = buffer[consumed..];
                        }
                    }
                }
            }

            try
            {
                await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    private static byte[] Concat(byte[] a, ReadOnlySpan<byte> b)
    {
        if (a.Length == 0) return b.ToArray();
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result.AsSpan(a.Length));
        return result;
    }

    /// <summary>(start, length) of each \n-terminated line; excludes trailing \r.</summary>
    private static IEnumerable<(int Start, int Length)> CompleteLines(byte[] buffer)
    {
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != (byte)'\n')
                continue;
            var length = i - start;
            if (length > 0 && buffer[start + length - 1] == (byte)'\r')
                length--;
            yield return (start, length);
            start = i + 1;
        }
    }

    private string DecodeLine(ReadOnlySpan<byte> bytes) => _options.Encoding.GetString(bytes);
}
