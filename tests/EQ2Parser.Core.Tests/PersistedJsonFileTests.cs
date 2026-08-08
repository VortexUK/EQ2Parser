using EQ2Parser.Core.Persistence;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// The one way app state reaches disk: atomic writes, quarantine-on-bad-load,
/// debounce coalescing, direct-Save-cancels-pending, and the exit flush.
/// </summary>
public sealed class PersistedJsonFileTests : IDisposable
{
    private sealed record Payload(string Name, int Value);

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "eq2parser-tests", Guid.NewGuid().ToString("N"));

    private string PathFor(string name) => Path.Combine(_dir, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A straggling timer may still hold a handle — temp dir, best effort.
        }
    }

    [Fact]
    public void Save_Then_Load_Round_Trips()
    {
        var path = PathFor("settings.json");
        PersistedJsonFile.Save(path, new Payload("Alice", 42));

        var loaded = PersistedJsonFile.Load<Payload>(path, () => new Payload("fallback", 0));
        Assert.Equal(new Payload("Alice", 42), loaded);
        Assert.False(File.Exists(path + ".tmp"), "tmp must not survive a successful save");
    }

    [Fact]
    public void Missing_File_Returns_Fallback_Without_Creating_It()
    {
        var path = PathFor("absent.json");
        var loaded = PersistedJsonFile.Load<Payload>(path, () => new Payload("fallback", 7));
        Assert.Equal(7, loaded.Value);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Corrupt_File_Is_Quarantined_Not_Overwritten()
    {
        var path = PathFor("settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ not valid json");

        var loaded = PersistedJsonFile.Load<Payload>(path, () => new Payload("fallback", 0));

        Assert.Equal("fallback", loaded.Name);
        Assert.False(File.Exists(path), "bad file must be moved aside");
        Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Save_Overwrites_A_CrashOrphaned_Tmp()
    {
        var path = PathFor("settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path + ".tmp", "torn write from a previous crash");

        PersistedJsonFile.Save(path, new Payload("fresh", 1));

        Assert.Equal(1, PersistedJsonFile.Load<Payload>(path, () => new Payload("x", 0)).Value);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Load_Sweeps_PerThread_Tmp_Orphans_From_v028()
    {
        var path = PathFor("settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path + ".14.tmp", "orphan");
        File.WriteAllText(path + ".7.tmp", "orphan");

        PersistedJsonFile.Load<Payload>(path, () => new Payload("fallback", 0));

        Assert.Empty(Directory.GetFiles(_dir, "settings.json.*.tmp"));
    }

    [Fact]
    public async Task SaveSoon_Coalesces_To_One_Trailing_Write()
    {
        var path = PathFor("debounced.json");
        var calls = 0;
        for (var i = 1; i <= 5; i++)
        {
            var value = i;
            PersistedJsonFile.SaveSoon(path, () =>
            {
                Interlocked.Increment(ref calls);
                return new Payload("soon", value);
            });
        }
        Assert.False(File.Exists(path), "debounce must not write immediately");

        await WaitForFileAsync(path);
        // One write, and the LATEST factory's value — earlier factories are
        // superseded, never invoked.
        Assert.Equal(1, calls);
        Assert.Equal(5, PersistedJsonFile.Load<Payload>(path, () => new Payload("x", 0)).Value);
    }

    [Fact]
    public async Task Direct_Save_Cancels_The_Pending_Debounced_Write()
    {
        var path = PathFor("cancelled.json");
        var factoryRan = false;
        PersistedJsonFile.SaveSoon(path, () =>
        {
            factoryRan = true;
            return new Payload("stale", 1);
        });
        PersistedJsonFile.Save(path, new Payload("direct", 2));

        // Well past the 500ms debounce window: the pending write must not fire.
        await Task.Delay(900);
        Assert.False(factoryRan, "direct Save must cancel the pending debounced write");
        Assert.Equal(2, PersistedJsonFile.Load<Payload>(path, () => new Payload("x", 0)).Value);
    }

    [Fact]
    public void FlushPending_Writes_Immediately_On_Exit()
    {
        var path = PathFor("flushed.json");
        PersistedJsonFile.SaveSoon(path, () => new Payload("exit", 9));
        Assert.False(File.Exists(path));

        PersistedJsonFile.FlushPending();

        Assert.Equal(9, PersistedJsonFile.Load<Payload>(path, () => new Payload("x", 0)).Value);
    }

    [Fact]
    public void Quarantine_Keeps_Only_The_Newest_Five()
    {
        var path = PathFor("settings.json");
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < 7; i++)
            File.WriteAllText($"{path}.corrupt-2026010{i}-000000", "old");
        File.WriteAllText(path, "{ bad json");

        PersistedJsonFile.Load<Payload>(path, () => new Payload("fallback", 0));

        Assert.Equal(5, Directory.GetFiles(_dir, "settings.json.corrupt-*").Length);
    }

    private static async Task WaitForFileAsync(string path)
    {
        // Debounce is 500ms; poll up to 5s so a slow CI runner never flakes.
        for (var waited = 0; waited < 5_000; waited += 50)
        {
            if (File.Exists(path))
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"debounced write never landed: {path}");
    }
}
