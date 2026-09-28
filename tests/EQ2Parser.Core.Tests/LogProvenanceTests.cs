using EQ2Parser.Core.Logs;
using EQ2Parser.Core.Upload;

namespace EQ2Parser.Core.Tests;

public sealed class LogFileHoldersTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eq2parser-holders-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string FilePath => Path.Combine(_dir, "eq2log_Probe.txt");

    [Fact]
    public void Sees_Our_Own_Open_Handle()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var stream = File.Open(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var holders = LogFileHolders.Probe(FilePath);
        Assert.Contains(holders, h => h.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public void Unheld_File_Does_Not_List_Us()
    {
        if (!OperatingSystem.IsWindows())
            return;
        File.WriteAllText(FilePath, "closed again\n");
        Assert.DoesNotContain(LogFileHolders.Probe(FilePath), h => h.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public void Missing_File_Is_Empty_Not_An_Error() =>
        Assert.Empty(LogFileHolders.Probe(Path.Combine(_dir, "never-existed.txt")));
}

public class LogProvenanceTests
{
    private const int OwnPid = 1111;

    private static FileHolder H(int pid, string name) => new(pid, name);

    [Fact]
    public void Eq2_Holding_The_Log_Is_The_Verified_Stamp()
    {
        var warnings = LogProvenance.BuildWarnings(
            [H(OwnPid, "EQ2Parser.App"), H(2222, "EverQuest2")], OwnPid);
        Assert.Equal([LogProvenance.WriterVerified], warnings);
    }

    [Fact]
    public void Eq2_Match_Is_Case_Insensitive()
    {
        var warnings = LogProvenance.BuildWarnings([H(2222, "everquest2")], OwnPid);
        Assert.Equal([LogProvenance.WriterVerified], warnings);
    }

    [Fact]
    public void Nobody_Else_Holding_It_Is_Unverified()
    {
        // Only our own tail-reader handle — a backlog parse after the game
        // closed. Informative, not damning.
        var warnings = LogProvenance.BuildWarnings([H(OwnPid, "EQ2Parser.App")], OwnPid);
        Assert.Equal([LogProvenance.WriterUnverified], warnings);
    }

    [Fact]
    public void Act_Alongside_Eq2_Is_Expected_Not_Foreign()
    {
        // The 99% case: ACT tailing the same log. No foreign marker.
        var warnings = LogProvenance.BuildWarnings(
            [H(2222, "EverQuest2"), H(3333, "Advanced Combat Tracker")], OwnPid);
        Assert.Equal([LogProvenance.WriterVerified], warnings);
    }

    [Fact]
    public void Act_Match_Is_Case_Insensitive_And_Counts_For_Unverified_Too()
    {
        var warnings = LogProvenance.BuildWarnings([H(3333, "advanced combat tracker")], OwnPid);
        Assert.Equal([LogProvenance.WriterUnverified], warnings);
    }

    [Fact]
    public void Any_Other_Holder_Is_One_Bare_Marker_Never_A_Name()
    {
        var warnings = LogProvenance.BuildWarnings(
            [
                H(2222, "EverQuest2"),
                H(3333, "Advanced Combat Tracker"),
                H(4444, "notepad"), H(5555, "notepad"), H(6666, "SomeAntivirus"),
            ],
            OwnPid);
        Assert.Equal([LogProvenance.WriterVerified, LogProvenance.ForeignHolder], warnings);
        Assert.DoesNotContain(warnings, w => w.Contains("notepad", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(warnings, w => w.Contains("SomeAntivirus", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(warnings, w => w.Contains(':'));
    }

    [Fact]
    public void Foreign_Marker_Stays_Under_The_Server_Cap()
    {
        var warnings = LogProvenance.BuildWarnings([H(2222, new string('x', 100))], OwnPid);
        Assert.All(warnings, w => Assert.True(w.Length <= 64, $"server caps entries at 64 chars, got {w.Length}"));
    }
}
