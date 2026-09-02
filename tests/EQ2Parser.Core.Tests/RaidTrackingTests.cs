using EQ2Parser.Core.Raid;
using EQ2Parser.Core.Upload;

namespace EQ2Parser.Core.Tests;

/// <summary>Raid-attendance building blocks: /who block parsing (verbatim
/// live shapes), roster accumulation from deltas + presence + fight allies,
/// the positional who-pair classification, DKP command files, and
/// install-dir derivation.</summary>
public sealed class RaidTrackingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 25, 21, 4, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    // ── WhoParser (verbatim corpus lines, 2026-06-25 raid night) ────────────

    [Fact]
    public void Who_Block_Parses_Detail_Anonymous_And_Afk_Rows()
    {
        var p = new WhoParser();
        Assert.Null(p.Feed("/who search results for Throne of New Tunaria:", T0));
        Assert.Null(p.Feed("------------------------------------------", T0));
        Assert.Null(p.Feed("[70 Conjuror] Tsuna (Freeblood) <Exordium> Zone: Throne of New Tunaria", T0));
        Assert.Null(p.Feed("[Anonymous] Betabonk", T0));
        Assert.Null(p.Feed("[Anonymous] Ashtar (AFK)", T0));
        Assert.Null(p.Feed("[70 Fury] Vessix (Arasai) <Gin and Jumjum> Zone: The City of Freeport", T0));
        var result = p.Feed("4 players found", T0);

        Assert.NotNull(result);
        Assert.Equal("Throne of New Tunaria", result.ZoneFilter);
        Assert.Equal(4, result.Rows.Count);
        var tsuna = result.Rows[0];
        Assert.Equal(("Tsuna", 70, "Conjuror", "Freeblood", "Exordium"), (tsuna.Name, tsuna.Level, tsuna.Class, tsuna.Race, tsuna.Guild));
        Assert.False(tsuna.Afk);
        Assert.True(result.Rows[2].Afk);           // Ashtar (AFK)
        Assert.Null(result.Rows[1].Level);         // anonymous rows carry name only
    }

    [Fact]
    public void Who_Singular_Footer_And_Plain_Header()
    {
        var p = new WhoParser();
        Assert.Null(p.Feed("/who search results:", T0));
        Assert.Null(p.Feed("[Anonymous] Ashtar (AFK)", T0));
        var result = p.Feed("1 player found", T0);
        Assert.NotNull(result);
        Assert.Null(result.ZoneFilter);
        Assert.True(Assert.Single(result.Rows).Afk);
    }

    [Fact]
    public void Who_Block_Abandoned_After_Timeout()
    {
        var p = new WhoParser();
        p.Feed("/who search results:", T0);
        p.Feed("[Anonymous] Betabonk", T0);
        // A footer arriving way later belongs to some other output.
        Assert.Null(p.Feed("1 player found", At(30)));
    }

    // ── RaidRosterTracker ───────────────────────────────────────────────────

    [Fact]
    public void Raid_Deltas_And_Presence_Accumulate()
    {
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        t.OnLine("Shadynecro has joined the raid.", At(1));
        t.OnLine("Betabonk's group has joined the raid.", At(2));
        t.OnLine("Guildmate: Coyi has logged in.", At(3));
        t.OnLine("Shadynecro has left the raid.", At(60));
        t.OnLine("Distraction's group has left the raid, Betabonk is now the raid leader.", At(61));
        t.OnLine("Guildmate: Coyi has logged out.", At(90));

        var by = t.Snapshot().ToDictionary(m => m.Name);
        Assert.False(by["Shadynecro"].InRaid);
        Assert.True(by["Shadynecro"].Online);        // left raid, still online
        Assert.True(by["Betabonk"].InRaid);
        Assert.False(by["Distraction"].InRaid);      // group-leave variant with leader suffix
        Assert.False(by["Coyi"].Online);
        Assert.False(by.ContainsKey("Friend"));      // nothing bogus
    }

    [Fact]
    public void Fight_Allies_Catch_PreJoin_Members()
    {
        // Martyn was in the raid before our own join — no delta ever names
        // him joining, but he appears in a fight's ally set.
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        t.OnFightAllies(["Martyn", "a krait patriarch", "Betabonk"], At(300));
        var by = t.Snapshot().ToDictionary(m => m.Name);
        Assert.True(by["Martyn"].InRaid);
        Assert.True(by["Betabonk"].InRaid);
        Assert.False(by.ContainsKey("a krait patriarch")); // articled mob filtered
    }

    [Fact]
    public void Who_Pair_Classifies_Raid_Then_Guild()
    {
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        // Block 1 (raid): Tsuna. Block 2 within the pair window (guild): Coyi.
        t.OnLine("/who search results:", At(0));
        t.OnLine("[70 Conjuror] Tsuna (Freeblood) <Exordium> Zone: Throne of New Tunaria", At(0));
        t.OnLine("1 player found", At(0));
        t.OnLine("/who search results:", At(2));
        t.OnLine("[70 Templar] Coyi (High Elf) <Exordium> Zone: Qeynos Harbor", At(2));
        t.OnLine("1 player found", At(2));

        var by = t.Snapshot().ToDictionary(m => m.Name);
        Assert.True(by["Tsuna"].InRaid);              // first of the pair = raid seed
        Assert.False(by["Coyi"].InRaid);              // second = online guildies
        Assert.True(by["Coyi"].Online);
        Assert.Equal("Templar", by["Coyi"].Class);
    }

    [Fact]
    public void Lone_Who_Block_Is_Online_Evidence_Only()
    {
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        t.OnLine("/who search results:", At(0));
        t.OnLine("[70 Conjuror] Tsuna (Freeblood) <Exordium> Zone: Throne of New Tunaria", At(0));
        t.OnLine("1 player found", At(0));

        var m = Assert.Single(t.Snapshot(), x => x.Name == "Tsuna");
        Assert.False(m.InRaid);
        Assert.True(m.Online);
    }

    [Fact]
    public void Prefilter_Accepts_All_Signal_Shapes()
    {
        Assert.True(RaidRosterTracker.LooksRelevant("Shadynecro has joined the raid."));
        Assert.True(RaidRosterTracker.LooksRelevant("Guildmate: Coyi has logged in."));
        Assert.True(RaidRosterTracker.LooksRelevant("/who search results:"));
        Assert.True(RaidRosterTracker.LooksRelevant("[Anonymous] Betabonk"));
        Assert.True(RaidRosterTracker.LooksRelevant("24 players found"));
        Assert.False(RaidRosterTracker.LooksRelevant("YOU hit a training dummy for 100 points of crushing damage."));
    }

    // ── DkpCommandFile ──────────────────────────────────────────────────────

    [Fact]
    public void Award_File_Has_Raid_Grant_Plus_SitOuts()
    {
        var text = DkpCommandFile.BuildAward(5, "Raid DKP: end of raid", ["Menludiir", "Coyi", "a mob"]);
        var lines = text.TrimEnd().Split("\r\n");
        Assert.Equal("/guild points add 5 raid Raid DKP: end of raid", lines[0]);
        Assert.Equal("/guild points add 5 Coyi Raid DKP: end of raid", lines[1]);
        Assert.Equal("/guild points add 5 Menludiir Raid DKP: end of raid", lines[2]);
        Assert.Equal(3, lines.Length); // "a mob" filtered by the player-name shape
    }

    [Fact]
    public void Award_Reason_Is_Sanitised()
    {
        var text = DkpCommandFile.BuildAward(3, "/quit\r\nhaha", []);
        Assert.Equal("/guild points add 3 raid quit haha\r\n", text);
    }

    [Fact]
    public void Refresh_File_Is_The_Ordered_Pair()
    {
        Assert.Equal("/who all raid\r\n/who all guild\r\n", DkpCommandFile.BuildRefresh());
    }

    // ── LogPaths.ParseInstallDir ────────────────────────────────────────────

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\EverQuest 2\logs\Varsoon\eq2log_Menludiir.txt", @"D:\SteamLibrary\steamapps\common\EverQuest 2")]
    [InlineData(@"C:\EQ2\logs\eq2log.txt", @"C:\EQ2")]
    [InlineData(@"C:\SomewhereElse\eq2log_Menludiir.txt", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseInstallDir_Cases(string? logPath, string? expected)
    {
        Assert.Equal(expected, LogPaths.ParseInstallDir(logPath));
    }
}
