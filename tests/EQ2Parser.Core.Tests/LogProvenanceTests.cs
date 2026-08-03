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
    public void Foreign_Holders_Are_Named_Deduped_And_Capped()
    {
        var warnings = LogProvenance.BuildWarnings(
            [
                H(2222, "EverQuest2"),
                H(3333, "notepad"), H(4444, "notepad"), // same name twice → once
                H(5555, "a"), H(6666, "b"), H(7777, "c"), H(8888, "d"), H(9999, "e"),
            ],
            OwnPid);
        Assert.Equal(LogProvenance.WriterVerified, warnings[0]);
        Assert.Single(warnings, w => w == $"{LogProvenance.ForeignHolderPrefix}notepad");
        Assert.Equal(5, warnings.Count); // stamp + 4 foreign (capped), dedup dropped one
    }

    [Fact]
    public void Foreign_Names_Are_Truncated_Under_The_Server_Cap()
    {
        var warnings = LogProvenance.BuildWarnings([H(2222, new string('x', 100))], OwnPid);
        var foreign = Assert.Single(warnings, w => w.StartsWith(LogProvenance.ForeignHolderPrefix, StringComparison.Ordinal));
        Assert.True(foreign.Length <= 64, $"server caps entries at 64 chars, got {foreign.Length}");
    }
}
