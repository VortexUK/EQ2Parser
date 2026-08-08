using System.IO;
using EQ2Parser.App.Services;

// The app-state services (AppSettings, TriggerService, TimerService,
// LexiconSyncService) all persist under AppSettings.Directory, which reads
// the process-wide EQ2PARSER_DATA_DIR override. That makes isolation
// process-global state — so this assembly runs sequentially, and each test
// class redirects to its own fresh directory via TestDataDir.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace EQ2Parser.App.Tests;

/// <summary>Per-test-class sandbox for %LOCALAPPDATA%\EQ2Parser. Construct
/// in the test class ctor; every service constructed afterwards reads and
/// writes ONLY inside the sandbox — a test can never touch (or be polluted
/// by) a real install's settings, triggers, or token.</summary>
public sealed class TestDataDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "eq2parser-app-tests", Guid.NewGuid().ToString("N"));

    public TestDataDir()
    {
        Directory.CreateDirectory(Path);
        Environment.SetEnvironmentVariable("EQ2PARSER_DATA_DIR", Path);
        // Fail loudly if the override ever stops being honoured — writing
        // to the real profile from tests must never happen silently.
        Assert.Equal(Path, AppSettings.Directory);
    }

    public string FileIn(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Environment.SetEnvironmentVariable("EQ2PARSER_DATA_DIR", null);
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A straggling handle — temp dir, best effort.
        }
    }
}
