using EQ2Parser.Core.Logs;

namespace EQ2Parser.Core.Tests;

/// <summary>Attach-time zone recovery — scanning the log tail behind the
/// start offset for the newest "You have entered …" line.</summary>
public class ZoneLookbehindTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"eq2log_Test.{Guid.NewGuid():N}.txt");

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

    private static string Line(long epoch, string message) =>
        $"({epoch})[Mon Jul 28 22:26:40 2026] {message}";

    [Fact]
    public void Finds_The_Newest_Zone_Line_Behind_The_Offset()
    {
        File.WriteAllLines(_path,
        [
            Line(100, "You have entered The Feerrott."),
            Line(200, "You hit a lizardman for 50 points of slashing damage."),
            Line(300, "You have entered Mistmoore's Inner Sanctum."),
            Line(400, "Mayong Mistmoore says, \"You dare?\""),
        ]);
        var zone = ZoneLookbehind.FindLastZone(_path, new FileInfo(_path).Length);
        Assert.Equal("Mistmoore's Inner Sanctum", zone);
    }

    [Fact]
    public void Only_Looks_Behind_The_Given_Offset()
    {
        var earlier = Line(100, "You have entered The Feerrott.") + Environment.NewLine;
        var later = Line(300, "You have entered Mistmoore's Inner Sanctum.") + Environment.NewLine;
        File.WriteAllText(_path, earlier + later);
        // Offset ends right after the first line — the later zone is ahead
        // of the tail position and must not be seen.
        var zone = ZoneLookbehind.FindLastZone(_path, earlier.Length);
        Assert.Equal("The Feerrott", zone);
    }

    [Fact]
    public void No_Zone_Line_Or_Missing_File_Is_Null()
    {
        File.WriteAllLines(_path, [Line(100, "You hit a rat for 3 points of crushing damage.")]);
        Assert.Null(ZoneLookbehind.FindLastZone(_path, new FileInfo(_path).Length));
        Assert.Null(ZoneLookbehind.FindLastZone(_path + ".missing", 1000));
        Assert.Null(ZoneLookbehind.FindLastZone(_path, 0));
    }

    [Fact]
    public void Bounded_Window_Skips_Zone_Lines_Older_Than_The_Cap()
    {
        // A zone line, then enough filler to push it outside a small window.
        using (var w = new StreamWriter(_path))
        {
            w.WriteLine(Line(100, "You have entered The Feerrott."));
            for (var i = 0; i < 200; i++)
                w.WriteLine(Line(200 + i, "You hit a lizardman for 50 points of slashing damage."));
        }
        var length = new FileInfo(_path).Length;
        Assert.Null(ZoneLookbehind.FindLastZone(_path, length, maxWindowBytes: 1024));
        Assert.Equal("The Feerrott", ZoneLookbehind.FindLastZone(_path, length));
    }

    [Fact]
    public void Reads_While_The_File_Is_Held_Open_For_Writing()
    {
        using var writer = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.ReadWrite);
        using (var w = new StreamWriter(writer, leaveOpen: true))
        {
            w.WriteLine(Line(100, "You have entered Kael Drakkel."));
            w.Flush();
        }
        Assert.Equal("Kael Drakkel", ZoneLookbehind.FindLastZone(_path, new FileInfo(_path).Length));
    }
}
