using System.Collections.Concurrent;
using System.Text;
using EQ2Parser.Core.Logs;

namespace EQ2Parser.Core.Tests;

public sealed class LogTailReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eq2parser-tests-").FullName;
    private readonly CancellationTokenSource _cts = new();

    public void Dispose()
    {
        _cts.Cancel();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string LogPath => Path.Combine(_dir, "eq2log_Testchar.txt");

    private static readonly LogTailOptions FastPoll = new()
    {
        StartAtEnd = false,
        PollInterval = TimeSpan.FromMilliseconds(20),
    };

    private ConcurrentQueue<TailedLine> StartReader(LogTailOptions options)
    {
        var lines = new ConcurrentQueue<TailedLine>();
        var reader = new LogTailReader(LogPath, options);
        _ = Task.Run(async () =>
        {
            await foreach (var line in reader.ReadLinesAsync(_cts.Token))
                lines.Enqueue(line);
        });
        return lines;
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private void AppendBytes(byte[] bytes)
    {
        using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes);
    }

    private void AppendText(string text) => AppendBytes(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Reads_Existing_And_Appended_Lines_From_Start()
    {
        AppendText("(1)[stamp] first\n");
        var lines = StartReader(FastPoll);
        await WaitFor(() => lines.Count == 1, "existing line");

        AppendText("(2)[stamp] second\n");
        await WaitFor(() => lines.Count == 2, "appended line");
        Assert.Equal(["(1)[stamp] first", "(2)[stamp] second"], lines.Select(l => l.Raw).ToArray());
    }

    [Fact]
    public async Task StartAtEnd_Skips_Preexisting_Content()
    {
        AppendText("(1)[stamp] old line\n");
        var lines = StartReader(FastPoll with { StartAtEnd = true });

        // Give the reader a few polls to (incorrectly) pick up the old line.
        await Task.Delay(150);
        Assert.Empty(lines);

        AppendText("(2)[stamp] live line\n");
        await WaitFor(() => !lines.IsEmpty, "live line");
        Assert.Equal(["(2)[stamp] live line"], lines.Select(l => l.Raw).ToArray());
    }

    [Fact]
    public async Task Never_Yields_A_Partial_Line()
    {
        var lines = StartReader(FastPoll);
        AppendText("(1)[stamp] half");
        await Task.Delay(150);
        Assert.Empty(lines);

        AppendText(" and the rest\r\n");
        await WaitFor(() => !lines.IsEmpty, "completed line");
        Assert.Equal(["(1)[stamp] half and the rest"], lines.Select(l => l.Raw).ToArray());
    }

    [Fact]
    public async Task Multibyte_Character_Split_Across_Writes_Survives()
    {
        var lines = StartReader(FastPoll);
        // "Menludiir застигнут" with the last Cyrillic char split mid-sequence.
        var full = Encoding.UTF8.GetBytes("(1)[stamp] Menludiir застигнут\n");
        AppendBytes(full[..^3]); // cuts inside the final multi-byte char
        await Task.Delay(150);
        AppendBytes(full[^3..]);

        await WaitFor(() => !lines.IsEmpty, "reassembled line");
        Assert.Equal(["(1)[stamp] Menludiir застигнут"], lines.Select(l => l.Raw).ToArray());
    }

    [Fact]
    public async Task Truncated_File_Restarts_From_Zero()
    {
        AppendText("(1)[stamp] before rotation\n");
        var lines = StartReader(FastPoll);
        await WaitFor(() => lines.Count == 1, "pre-rotation line");

        // Simulate log rotation: replace with a shorter file.
        File.WriteAllText(LogPath, "(2)[stamp] after rotation\n");
        await WaitFor(() => lines.Count == 2, "post-rotation line");
        Assert.Equal("(2)[stamp] after rotation", lines.Last().Raw);
    }

    [Fact]
    public async Task Waits_For_File_That_Does_Not_Exist_Yet()
    {
        var lines = StartReader(FastPoll);
        await Task.Delay(100); // reader polling a missing file must not throw
        AppendText("(1)[stamp] born late\n");
        await WaitFor(() => !lines.IsEmpty, "line from late-created file");
        Assert.Equal(["(1)[stamp] born late"], lines.Select(l => l.Raw).ToArray());
    }

    [Fact]
    public async Task Import_Backlog_Is_Read_In_Bounded_Chunks_With_Lines_Split_Across_Them()
    {
        // Regression: import used to read the ENTIRE backlog into one
        // byte[] — multi-GB logs threw and silently killed the source. A
        // tiny chunk size proves the chunked drain reassembles lines that
        // straddle chunk boundaries (including a multi-byte UTF-8 char).
        var expected = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < 200; i++)
        {
            var line = $"(1)[stamp] ability Frostbiteé number {i} hits the target";
            expected.Add(line);
            sb.Append(line).Append('\n');
        }
        AppendText(sb.ToString());

        var lines = StartReader(FastPoll with { ChunkBytes = 16 });
        await WaitFor(() => lines.Count >= expected.Count, "all chunked backlog lines");
        Assert.Equal(expected, lines.Select(l => l.Raw).ToArray());
    }

    [Fact]
    public async Task Overlong_Complete_Line_Is_Dropped_But_Following_Lines_Read()
    {
        // A hostile/malformed newline-terminated line far longer than any real
        // EQ2 line must NOT be fed to the O(n²) grammar; it's dropped and the
        // next line still parses.
        AppendText(new string('X', 200) + "\n(1)[stamp] real line\n");
        var lines = StartReader(FastPoll with { MaxLineBytes = 32 });
        await WaitFor(() => lines.Any(l => l.Raw == "(1)[stamp] real line"), "line after an over-long line");
        Assert.DoesNotContain(lines, l => l.Raw.StartsWith('X'));
    }

    [Fact]
    public async Task Newlineless_Overlong_Carry_Is_Discarded_Then_Recovers()
    {
        // A giant run with no newline must not grow the carry buffer without
        // bound (OOM); it's discarded, and the reader recovers at the next
        // newline.
        var lines = StartReader(FastPoll with { MaxLineBytes = 32, ChunkBytes = 16 });
        AppendBytes(Encoding.UTF8.GetBytes(new string('Y', 200))); // no newline → over-long carry
        await Task.Delay(80);
        AppendText("junk\n(1)[stamp] after\n"); // recover at the newline
        await WaitFor(() => lines.Any(l => l.Raw == "(1)[stamp] after"), "recovery after over-long carry");
        Assert.DoesNotContain(lines, l => l.Raw.Contains('Y'));
    }
}
