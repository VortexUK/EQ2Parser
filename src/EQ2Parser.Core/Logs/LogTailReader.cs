using System.Runtime.CompilerServices;
using System.Text;

namespace EQ2Parser.Core.Logs;

/// <summary>Options for <see cref="LogTailReader"/>.</summary>
public sealed record LogTailOptions
{
    /// <summary>Start at the end of the file (live mode) instead of the beginning (import mode).</summary>
    public bool StartAtEnd { get; init; } = true;

    /// <summary>Resume from a byte position saved by a previous session
    /// (wins over <see cref="StartAtEnd"/>). A stale offset past the file's
    /// length is treated as rotation — restart from 0.</summary>
    public long? StartOffset { get; init; }

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

    /// <summary>Largest single read. Bounds import memory — a whole-backlog
    /// read used to allocate multi-GB buffers (and throw outright past
    /// Array.MaxLength, silently killing the source). Tests shrink it to
    /// exercise cross-chunk line reassembly.</summary>
    public int ChunkBytes { get; init; } = 1 << 20;

    /// <summary>Longest line the reader will assemble. A real EQ2 log line is
    /// well under 2 KB; a line that keeps growing with no newline is a
    /// malformed/hostile file — it would grow the carry buffer without bound
    /// (OOM) and feed a huge string to the O(n²) grammar regexes. Past this,
    /// the in-progress line is discarded and the reader skips to the next
    /// newline. Tests shrink it to exercise the skip path.</summary>
    public int MaxLineBytes { get; init; } = 16 * 1024;
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
    private long _consumedPosition;

    /// <summary>Byte position up to which COMPLETE lines have been yielded
    /// (a trailing partial line is not counted, so resuming here re-reads
    /// it whole). Persist this to continue across sessions.</summary>
    public long ConsumedPosition => Interlocked.Read(ref _consumedPosition);

    /// <summary>Raw complete lines (no trailing CR/LF) with arrival stamps,
    /// forever until cancelled.</summary>
    public async IAsyncEnumerable<TailedLine> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        long position = -1; // -1 = not yet initialised (resolve on first sight of the file)
        var skipOverlong = false; // dropping bytes of an over-long line until the next newline
        var carry = Array.Empty<byte>(); // partial-line bytes from the previous read

        while (!ct.IsCancellationRequested)
        {
            if (File.Exists(path) && !IdleFastPath(position))
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
                        {
                            position = _options.StartOffset ?? (_options.StartAtEnd ? stream.Length : 0);
                            Interlocked.Exchange(ref _consumedPosition, Math.Min(position, stream.Length));
                        }
                        if (stream.Length < position)
                        {
                            // Truncated or rotated: start over, drop any carry.
                            position = 0;
                            carry = [];
                            skipOverlong = false;
                            Interlocked.Exchange(ref _consumedPosition, 0);
                        }

                        // Drain in bounded chunks: never allocate the whole
                        // backlog (multi-GB on import), never wait on the
                        // poll interval between chunks of the same backlog.
                        while (stream.Length > position && !ct.IsCancellationRequested)
                        {
                            stream.Position = position;
                            var want = (int)Math.Min(_options.ChunkBytes, stream.Length - position);
                            var fresh = new byte[want];
                            var read = await stream.ReadAsync(fresh.AsMemory(), ct).ConfigureAwait(false);
                            if (read == 0)
                                break;
                            position += read;
                            // One arrival stamp per read batch: lines flushed
                            // together genuinely arrived together.
                            var observedAt = _options.Clock();

                            var buffer = Concat(carry, fresh.AsSpan(0, read));

                            // Mid-skip of an over-long line: drop everything up
                            // to the next newline before parsing anything.
                            if (skipOverlong)
                            {
                                var nl = Array.IndexOf(buffer, (byte)'\n');
                                if (nl < 0)
                                {
                                    // Whole buffer is still part of the giant line — discard, keep skipping.
                                    carry = [];
                                    Interlocked.Exchange(ref _consumedPosition, position);
                                    continue;
                                }
                                skipOverlong = false;
                                buffer = buffer[(nl + 1)..]; // rare recovery-path slice
                            }

                            var consumed = 0;
                            foreach (var (start, length) in CompleteLines(buffer))
                            {
                                consumed = start + length;
                                // Drop a complete but absurdly long line (a real EQ2 line is
                                // < 2 KB) rather than feed it to the O(n²) grammar. The
                                // carry cap below covers the newline-less (unbounded) case.
                                if (length <= _options.MaxLineBytes)
                                    yield return new TailedLine(DecodeLine(buffer.AsSpan(start, length)), observedAt);
                            }
                            // Skip the newline byte(s) belonging to the last line.
                            while (consumed < buffer.Length && (buffer[consumed] == (byte)'\n' || buffer[consumed] == (byte)'\r'))
                                consumed++;
                            carry = buffer[consumed..];
                            // A trailing partial line longer than any real log line means a
                            // malformed/hostile file with no newlines — discard it and skip to
                            // the next newline instead of growing `carry` unbounded (OOM) and
                            // feeding a huge string to the O(n²) grammar.
                            if (carry.Length > _options.MaxLineBytes)
                            {
                                carry = [];
                                skipOverlong = true;
                            }
                            Interlocked.Exchange(ref _consumedPosition, position - carry.Length);
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

    /// <summary>Skip the open/close pair when the file hasn't changed size —
    /// the 10ms poll otherwise costs ~100 CreateFile/CloseHandle pairs per
    /// second per dormant source. Growth, shrink (rotation), and stat
    /// failures all fall through to the full open path.</summary>
    private bool IdleFastPath(long position)
    {
        if (position < 0)
            return false;
        try
        {
            return new FileInfo(path).Length == position;
        }
        catch (Exception)
        {
            return false; // let the open path deal with whatever this is
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
