using System.IO;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.Tests;

/// <summary>
/// Settings back-compatibility — the classic desktop-app regression is a
/// new build silently dropping or resetting an old install's settings.json.
/// These pin the contract the PR template asks contributors to keep: old
/// shapes load, unknown fields are ignored, absent fields get defaults.
/// </summary>
public sealed class AppSettingsCompatTests : IDisposable
{
    private readonly TestDataDir _dir = new();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dir.Dispose();
    }

    private void WriteSettings(string json) => File.WriteAllText(_dir.FileIn("settings.json"), json);

    [Fact]
    public void No_File_Loads_Defaults()
    {
        var settings = AppSettings.Load();
        Assert.Empty(settings.Sources);
        Assert.Empty(settings.WatchedFolders);
    }

    [Fact]
    public void Minimal_Old_Shape_Loads_With_Defaults_For_Absent_Fields()
    {
        // An early-release file: just sources, nothing else.
        WriteSettings("""
        {
          "Sources": [ { "Path": "C:\\logs\\Varsoon\\eq2log_Alice.txt", "ParseFromStart": false } ]
        }
        """);
        var settings = AppSettings.Load();

        var source = Assert.Single(settings.Sources);
        Assert.Equal("C:\\logs\\Varsoon\\eq2log_Alice.txt", source.Path);
        // Absent nullable resume position = "tail from the end" (the
        // documented pre-LastPosition behaviour), not zero.
        Assert.Null(source.LastPosition);
        Assert.False(source.AutoDiscovered);
        // Fields added after this file was written get their defaults —
        // e.g. the mini parse fade arrives enabled at 5s, not off/0.
        Assert.True(settings.MiniParseFadeEnabled);
        Assert.Equal(5, settings.MiniParseFadeSeconds);
    }

    [Fact]
    public void Unknown_Future_Fields_Are_Ignored_Not_Fatal()
    {
        // A DOWNGRADE scenario: a newer build wrote fields this build has
        // never heard of. Loading must not throw or quarantine.
        WriteSettings("""
        {
          "WatchedFolders": [ "C:\\logs" ],
          "SomeFutureFeatureFlag": true,
          "NestedFutureThing": { "a": [1, 2, 3] }
        }
        """);
        var settings = AppSettings.Load();

        Assert.Equal(["C:\\logs"], settings.WatchedFolders);
        Assert.False(File.Exists(_dir.FileIn("settings.json") + ".corrupt-*"));
    }

    [Fact]
    public void Legacy_Single_Overlay_Fields_Still_Deserialize()
    {
        // Pre-multi-overlay releases persisted one overlay's shape in flat
        // fields; OverlayController migrates them one-way on restore.
        WriteSettings("""
        {
          "OverlayVisible": true,
          "OverlayLocked": true,
          "OverlayLeft": 100.5,
          "OverlayTop": 200.25
        }
        """);
        var settings = AppSettings.Load();

        Assert.True(settings.OverlayVisible);
        Assert.True(settings.OverlayLocked);
        Assert.Equal(100.5, settings.OverlayLeft);
        Assert.Equal(200.25, settings.OverlayTop);
    }

    [Fact]
    public void Corrupt_File_Quarantines_And_Falls_Back_To_Defaults()
    {
        WriteSettings("{ definitely not json");
        var settings = AppSettings.Load();

        Assert.Empty(settings.Sources);
        // The bytes survive for recovery instead of being overwritten.
        Assert.Single(Directory.GetFiles(_dir.Path, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Save_Load_Round_Trip_Preserves_Values()
    {
        var settings = AppSettings.Load() with
        {
            WatchedFolders = ["C:\\logs\\Varsoon"],
            Sources = [new SourceSetting("C:\\logs\\Varsoon\\eq2log_Alice.txt", ParseFromStart: true, LastPosition: 12345, AutoDiscovered: true)],
            LanguageCode = "de",
        };
        settings.Save();
        var reloaded = AppSettings.Load();

        Assert.Equal(settings.WatchedFolders, reloaded.WatchedFolders);
        Assert.Equal(settings.Sources, reloaded.Sources);
        Assert.Equal("de", reloaded.LanguageCode);
    }
}
