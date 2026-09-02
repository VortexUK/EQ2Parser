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
    public void Plain_Who_Blocks_Are_Ignored_Without_A_Whoraid_Partner()
    {
        // Live bug 2026-09-02: a manual "/who all" (arbitrary server
        // players, same header shape as the guild who) was polluting the
        // sit-out list. Plain blocks now count ONLY as the guild half of
        // the whoraid pair — alone (or paired with each other) they are
        // ignored entirely.
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        t.OnLine("/who search results:", At(0));
        t.OnLine("[70 Conjuror] Tsuna (Freeblood) <Exordium> Zone: Throne of New Tunaria", At(0));
        t.OnLine("1 player found", At(0));
        t.OnLine("/who search results:", At(2));
        t.OnLine("[70 Templar] Coyi (High Elf) <Exordium> Zone: Qeynos Harbor", At(2));
        t.OnLine("1 player found", At(2));

        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Zone_Who_During_A_Session_Does_Not_Touch_The_Roster()
    {
        // A "/who" of the current zone mid-raid (strangers + a guildie who
        // IS raiding) must change nothing — not even for known members.
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        t.OnLine("Shadynecro has joined the raid.", At(0));
        t.OnLine("/who search results for The Commonlands:", At(60));
        t.OnLine("[70 Brigand] Xantos (Dark Elf) <Some Other Guild> Zone: The Commonlands", At(60));
        t.OnLine("[70 Necromancer] Shadynecro (Gnome) <Paragon> Zone: The Commonlands", At(60));
        t.OnLine("2 players found", At(60));

        var by = t.Snapshot().ToDictionary(m => m.Name);
        Assert.False(by.ContainsKey("Xantos"));       // stranger never enters the roster
        Assert.True(by["Shadynecro"].InRaid);         // known member untouched
    }

    [Fact]
    public void Whoraid_Then_Guild_Pair_Classifies_From_Live_Macro_Output()
    {
        // Verbatim lines from the live macro run 2026-09-02 (the whoraid
        // echo has its own header shape and its rows carry no guild/zone —
        // the original header regex missed it, dumping everyone into the
        // guild-only lone-block path = the whole raid shown as sitting out).
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        t.OnLine("/whoraid search results for Qeynos Province District:", At(0));
        t.OnLine("----------------------------------------", At(0));
        t.OnLine("[70 Templar] Menludiir (Gnome)", At(0));
        t.OnLine("[70 Berserker] Badbang (Ogre)", At(0));
        t.OnLine("[70 Inquisitor] Avacii (Dwarf)", At(0));
        t.OnLine("3 players found", At(0));
        t.OnLine("/who search results:", At(1));
        t.OnLine("[70 Coercer] Adomia (Ratonga) <Paragon> (AFK) Zone: The Feerrott", At(1));
        t.OnLine("[70 Berserker] Badbang (Ogre) <Paragon> Zone: The Sinking Sands", At(1));
        t.OnLine("[70 Wizard] Masqueraid (High Elf) <Paragon> Zone: Qeynos Capitol District", At(1));
        t.OnLine("[70 Illusionist] Neomi (Fae) <Paragon> Zone:    12 Qeynos Place", At(1));
        t.OnLine("10 players found", At(1));

        var by = t.Snapshot().ToDictionary(m => m.Name);
        Assert.True(by["Menludiir"].InRaid);
        Assert.True(by["Badbang"].InRaid);            // in both blocks — raid wins
        Assert.True(by["Avacii"].InRaid);
        Assert.False(by["Adomia"].InRaid);            // guild who = online only
        Assert.True(by["Adomia"].Online);
        Assert.True(by["Adomia"].Afk);                // (AFK) between guild tag and Zone
        Assert.False(by["Masqueraid"].InRaid);
        Assert.True(by.ContainsKey("Neomi"));         // house-address zone with padded spaces
        Assert.Equal("Templar", by["Menludiir"].Class); // whoraid rows still carry class

        // Guild-membership evidence from the pair: in the guild who = true;
        // in raid but absent from the guild who = provably not in guild.
        Assert.True(by["Badbang"].InGuild);
        Assert.True(by["Adomia"].InGuild);
        Assert.False(by["Menludiir"].InGuild);
        Assert.False(by["Avacii"].InGuild);
    }

    [Fact]
    public void Not_In_Guild_Is_Unknown_Without_A_Guild_Who_And_Sticky_Once_True()
    {
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        // Delta join + guildmate line, no who pair yet: no false conclusions.
        t.OnLine("Shadynecro has joined the raid.", At(0));
        t.OnLine("Guildmate: Coyi has logged in.", At(1));
        var by = t.Snapshot().ToDictionary(m => m.Name);
        Assert.Null(by["Shadynecro"].InGuild);        // unknown — still gets DKP (best effort)
        Assert.True(by["Coyi"].InGuild);

        // A later pair whose guild block misses Coyi must NOT downgrade the
        // sticky guildmate evidence; Shadynecro (absent) flips to false.
        t.OnLine("/whoraid search results for Veeshan's Peak:", At(10));
        t.OnLine("[70 Necromancer] Shadynecro (Gnome)", At(10));
        t.OnLine("1 player found", At(10));
        t.OnLine("/who search results:", At(11));
        t.OnLine("[70 Templar] Menludiir (Gnome) <Paragon> Zone: Veeshan's Peak", At(11));
        t.OnLine("1 player found", At(11));
        by = t.Snapshot().ToDictionary(m => m.Name);
        Assert.False(by["Shadynecro"].InGuild);
        Assert.True(by["Coyi"].InGuild);
    }

    [Fact]
    public void Lone_Whoraid_Block_Seeds_The_Raid_Immediately()
    {
        // whoraid is self-identifying — no guild partner needed for the
        // raid seed (the guild half only adds online/membership evidence).
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        t.OnLine("/whoraid search results for Veeshan's Peak:", At(0));
        t.OnLine("[70 Conjuror] Tsuna (Freeblood)", At(0));
        t.OnLine("1 player found", At(0));

        var m = Assert.Single(t.Snapshot(), x => x.Name == "Tsuna");
        Assert.True(m.InRaid);
        Assert.Null(m.InGuild); // no guild who yet — no membership conclusions
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
    public void Award_File_Without_Mains_Has_Raid_Grant_Plus_SitOuts()
    {
        var text = DkpCommandFile.BuildAward(5, "Raid DKP: end of raid", ["Whoever"], ["Menludiir", "Coyi", "a mob"]);
        var lines = text.TrimEnd().Split("\r\n");
        Assert.Equal("guild points add 5 raid Raid DKP: end of raid", lines[0]);
        Assert.Equal("guild points add 5 Coyi Raid DKP: end of raid", lines[1]);
        Assert.Equal("guild points add 5 Menludiir Raid DKP: end of raid", lines[2]);
        Assert.Equal(DkpCommandFile.MarkerCommand, lines[3]); // press-detection marker
        Assert.Equal(4, lines.Length); // "a mob" filtered by the player-name shape
    }

    [Fact]
    public void Award_Reason_Is_Sanitised()
    {
        var text = DkpCommandFile.BuildAward(3, "/quit\r\nhaha", [], []);
        Assert.Equal("guild points add 3 raid quit haha\r\neq2lexicon_dkp_done\r\n", text);
    }

    [Fact]
    public void Award_With_Mains_Grants_Individually_To_Mains()
    {
        // Alty is Mainy's raid alt; Tanky raids on their main. No bulk grant —
        // every award is individual and addressed to the MAIN.
        var mains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alty"] = "Mainy",
            ["Mainy"] = "Mainy",
            ["Tanky"] = "Tanky",
        };
        var text = DkpCommandFile.BuildAward(5, "DKP", ["Alty", "Tanky", "Pugsy"], [], mains);
        var lines = text.TrimEnd().Split("\r\n");
        Assert.Equal("guild points add 5 Mainy DKP", lines[0]);
        Assert.Equal("guild points add 5 Pugsy DKP", lines[1]); // unmapped pug → self
        Assert.Equal("guild points add 5 Tanky DKP", lines[2]);
        Assert.Equal(DkpCommandFile.MarkerCommand, lines[3]);
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public void Award_With_Mains_Dedupes_Main_And_Alt_Both_Present()
    {
        // Dual-boxing main + alt, and a sit-out alt whose main already got
        // the raid grant: exactly one award per main.
        var mains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alty"] = "Mainy",
            ["Mainy"] = "Mainy",
            ["Benchalt"] = "Tanky",
            ["Tanky"] = "Tanky",
        };
        var text = DkpCommandFile.BuildAward(5, "DKP", ["Mainy", "Alty", "Tanky"], ["Benchalt", "Coyi"], mains);
        var lines = text.TrimEnd().Split("\r\n");
        Assert.Equal("guild points add 5 Mainy DKP", lines[0]);
        Assert.Equal("guild points add 5 Tanky DKP", lines[1]);
        Assert.Equal("guild points add 5 Coyi DKP", lines[2]); // sit-out, unmapped → self
        Assert.Equal(DkpCommandFile.MarkerCommand, lines[3]);
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public void Award_With_Mains_Collapses_Two_Boxed_Characters_To_One_Grant()
    {
        // One player runs TWO characters in the raid (second account); their
        // main isn't even present. Both rows map to the main -> ONE award.
        var mains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alty"] = "Mainy",
            ["Boxling"] = "Mainy",
            ["Tanky"] = "Tanky",
        };
        var text = DkpCommandFile.BuildAward(5, "DKP", ["Alty", "Boxling", "Tanky"], [], mains);
        var lines = text.TrimEnd().Split("\r\n");
        Assert.Equal("guild points add 5 Mainy DKP", lines[0]);
        Assert.Equal("guild points add 5 Tanky DKP", lines[1]);
        Assert.Equal(DkpCommandFile.MarkerCommand, lines[2]);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void Award_With_Mains_Is_Case_Insensitive()
    {
        var mains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ALTY"] = "Mainy" };
        var text = DkpCommandFile.BuildAward(5, "DKP", ["alty"], [], mains);
        Assert.Equal("guild points add 5 Mainy DKP\r\neq2lexicon_dkp_done\r\n", text);
    }

    // ── DKP press-until-done loop (throttle discovered live 2026-09-02) ─────

    [Fact]
    public void Advance_Queue_Pops_The_Applied_Command()
    {
        var queue = new List<string> { "cmd A", "cmd B", "cmd C" };
        var (remaining, applied) = DkpCommandFile.AdvanceQueue(queue, failures: 2);
        Assert.Equal(1, applied);
        Assert.Equal(["cmd B", "cmd C"], remaining);
    }

    [Fact]
    public void Advance_Queue_Fully_Throttled_Press_Changes_Nothing()
    {
        var queue = new List<string> { "cmd A", "cmd B" };
        var (remaining, applied) = DkpCommandFile.AdvanceQueue(queue, failures: 2);
        Assert.Equal(0, applied);
        Assert.Equal(queue, remaining);
        // Stale/over-counted failures also never go negative.
        (_, applied) = DkpCommandFile.AdvanceQueue(queue, failures: 5);
        Assert.Equal(0, applied);
    }

    [Fact]
    public void Advance_Queue_Last_Command_Completes()
    {
        var (remaining, applied) = DkpCommandFile.AdvanceQueue(["cmd A"], failures: 0);
        Assert.Equal(1, applied);
        Assert.Empty(remaining);
    }

    [Fact]
    public void Queue_File_For_Empty_Queue_Is_Marker_Only()
    {
        Assert.Equal("eq2lexicon_dkp_done\r\n", DkpCommandFile.BuildQueueFile([]));
    }

    [Fact]
    public void Dkp_Progress_Counts_Failures_Per_Press()
    {
        var p = new DkpAwardProgress();
        var presses = new List<int>();
        p.PressDetected += presses.Add;

        // Press 1: 3 of 4 throttled, then the marker.
        for (var i = 0; i < 3; i++)
            p.OnLine(DkpCommandFile.ThrottleLogLine, T0);
        p.OnLine(DkpCommandFile.MarkerLogLine, T0);
        // Unrelated chatter between presses must not contaminate the count.
        p.OnLine("Guildmate: Coyi has logged in.", T0);
        // Press 2: everything applied (final command), marker only.
        p.OnLine(DkpCommandFile.MarkerLogLine, At(20));

        Assert.Equal([3, 0], presses);
    }

    [Fact]
    public void Dkp_Progress_Lines_Pass_The_Raid_Prefilter()
    {
        Assert.True(RaidRosterTracker.LooksRelevant(DkpCommandFile.ThrottleLogLine));
        Assert.True(RaidRosterTracker.LooksRelevant(DkpCommandFile.MarkerLogLine));
        Assert.False(RaidRosterTracker.LooksRelevant("Unknown command: 'somethingelse'"));
    }

    [Fact]
    public void Refresh_File_Is_The_Ordered_Pair()
    {
        Assert.Equal("whoraid\r\nwho all guild\r\n", DkpCommandFile.BuildRefresh());
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
