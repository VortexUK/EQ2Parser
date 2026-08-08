using EQ2Parser.Core.Logs;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// The epoch binary-search window reader behind "view log here" — budgeted
/// per side so a dense raid second can't starve the clicked line out of
/// the window. Untested while it lived in the App project.
/// </summary>
public sealed class LogWindowReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"eq2log_WindowTest.{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    private const long Base = 1_775_000_000;

    private static string Line(long epoch, string message) =>
        $"({epoch})[Mon Jul 28 22:26:40 2026] {message}";

    private void WriteLog(IEnumerable<string> lines) => File.WriteAllLines(_path, lines);

    [Fact]
    public void Reads_The_Window_Around_The_Target_Epoch()
    {
        WriteLog(Enumerable.Range(0, 100).Select(i => Line(Base + i, $"line {i}")));
        var window = LogWindowReader.Read(_path, Base + 50, beforeSeconds: 3, afterSeconds: 2);

        Assert.Equal(
            ["line 47", "line 48", "line 49", "line 50", "line 51", "line 52"],
            window.Select(l => l.Split("] ")[1]));
    }

    [Fact]
    public void Budget_Keeps_The_Last_Lines_Before_The_Target()
    {
        // 50 lines in the second before the target: the budget must keep
        // the LAST N before it, never fill up and drop the clicked second.
        List<string> lines = [.. Enumerable.Range(0, 50).Select(i => Line(Base + 9, $"noise {i}"))];
        lines.Add(Line(Base + 10, "the clicked swing"));
        WriteLog(lines);

        var window = LogWindowReader.Read(_path, Base + 10, beforeSeconds: 5, afterSeconds: 5, maxPerSide: 10);

        Assert.Equal(11, window.Count);
        Assert.Equal("noise 40", window[0].Split("] ")[1]); // last 10 noise lines kept
        Assert.Contains("the clicked swing", window[^1]);
    }

    [Fact]
    public void Binary_Search_Finds_The_Window_In_A_Large_File()
    {
        // Big enough that the search must actually narrow (well past the
        // 4096-byte terminal window).
        WriteLog(Enumerable.Range(0, 20_000).Select(i => Line(Base + i, $"padding line {i} {new string('x', 40)}")));
        var window = LogWindowReader.Read(_path, Base + 17_500, beforeSeconds: 1, afterSeconds: 1);

        Assert.Equal(3, window.Count);
        Assert.Contains("padding line 17500", window[1]);
    }

    [Fact]
    public void Malformed_Lines_Are_Skipped_Not_Fatal()
    {
        WriteLog([
            Line(Base, "good early"),
            "no epoch prefix at all",
            "(not-a-number)[ts] junk",
            Line(Base + 1, "good target"),
            Line(Base + 2, "good after"),
        ]);
        var window = LogWindowReader.Read(_path, Base + 1, beforeSeconds: 5, afterSeconds: 0);

        Assert.Equal(2, window.Count);
        Assert.Contains("good early", window[0]);
        Assert.Contains("good target", window[1]);
    }

    [Fact]
    public void Missing_File_Returns_Empty()
    {
        Assert.Empty(LogWindowReader.Read(_path + ".nope", Base, 5, 5));
    }
}
