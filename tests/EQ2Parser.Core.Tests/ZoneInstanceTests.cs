using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Grammar;
using EQ2Parser.Core.Logs;

namespace EQ2Parser.Core.Tests;

/// <summary>Zone-instance identity: the "This instance will expire in …"
/// lockout line stamps each encounter with its instance's expiry, so a
/// reset (fresh full lockout) is distinguishable from a re-entry.</summary>
public class ZoneInstanceTests
{
    [Theory]
    [InlineData("This instance will expire in 7 days.", 7 * 24 * 60)]
    [InlineData("This instance will expire in 3 days 23 hours.", (3 * 24 + 23) * 60)]
    [InlineData("This instance will expire in 9 days.", 9 * 24 * 60)]
    [InlineData("This instance will expire in 45 minutes.", 45)]
    [InlineData("This instance will expire in 1 day 2 hours 30 minutes.", 26 * 60 + 30)]
    public void Lockout_Line_Parses(string message, int expectedMinutes)
    {
        var parsed = Assert.IsType<InstanceLockoutEvent>(EnglishGrammar.TryParse(message));
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), parsed.Remaining);
    }

    [Theory]
    [InlineData("This instance will expire in soon.")]
    [InlineData("This instance will expire in -3 days.")]
    [InlineData("This instance will expire in 3 fortnights.")]
    [InlineData("The instance you are in is fine.")]
    public void Malformed_Lockouts_Do_Not_Parse(string message) =>
        Assert.Null(EnglishGrammar.TryParse(message) as InstanceLockoutEvent);

    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_785_000_000);

    private static void Feed(LogLineProcessor processor, long epoch, string message)
    {
        Assert.True(LogLine.TryParse($"({epoch})[Sat Aug 1 20:00:00 2026] {message}", out var line), message);
        processor.Process(line);
    }

    [Fact]
    public void Encounters_Carry_The_Instance_Expiry()
    {
        var engine = new ParserEngine("log", "Sofja");
        var processor = new LogLineProcessor(engine);
        var t = T0.ToUnixTimeSeconds();

        Feed(processor, t, "You have entered Trials of the Awakened.");
        Feed(processor, t, "This instance will expire in 7 days.");
        Assert.Equal(T0.AddDays(7), engine.ZoneInstanceExpiry);

        Feed(processor, t + 60, "YOU hit a sentinel of the Trial for 1,474 crushing damage.");
        engine.EndCombat();
        Assert.Equal(T0.AddDays(7), engine.History[^1].ZoneInstanceExpiry);

        // Re-entering the SAME instance two hours later: remaining shrinks,
        // computed expiry stays within the hour-truncation tolerance.
        Feed(processor, t + 7200, "You have entered Trials of the Awakened.");
        Feed(processor, t + 7200, "This instance will expire in 6 days 22 hours.");
        var reentry = engine.ZoneInstanceExpiry;
        Assert.NotNull(reentry);
        Assert.True((reentry.Value - T0.AddDays(7)).Duration() <= TimeSpan.FromMinutes(90));

        // A RESET: fresh full lockout → expiry ~2h later than the original.
        Feed(processor, t + 7300, "You have entered Trials of the Awakened.");
        Feed(processor, t + 7300, "This instance will expire in 7 days.");
        Assert.True((engine.ZoneInstanceExpiry!.Value - T0.AddDays(7)).Duration() > TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void Lockout_Line_Without_A_Recent_Zone_In_Is_Ignored()
    {
        var engine = new ParserEngine("log", "Sofja");
        var processor = new LogLineProcessor(engine);
        var t = T0.ToUnixTimeSeconds();

        Feed(processor, t, "You have entered Trials of the Awakened.");
        // 5 minutes later — not annotating the zone-in anymore.
        Feed(processor, t + 300, "This instance will expire in 7 days.");
        Assert.Null(engine.ZoneInstanceExpiry);
    }

    [Fact]
    public void Zone_Change_Clears_The_Old_Instance()
    {
        var engine = new ParserEngine("log", "Sofja");
        var processor = new LogLineProcessor(engine);
        var t = T0.ToUnixTimeSeconds();

        Feed(processor, t, "You have entered Trials of the Awakened.");
        Feed(processor, t, "This instance will expire in 7 days.");
        Feed(processor, t + 900, "You have entered Qeynos Province District.");
        Assert.Null(engine.ZoneInstanceExpiry);
    }

    [Fact]
    public void Lookbehind_Recovers_Zone_And_Expiry()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eq2log_Seed.{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllLines(path,
            [
                "(1785000000)[Sat Aug 1 20:00:00 2026] You have entered Trials of the Awakened.",
                "(1785000000)[Sat Aug 1 20:00:00 2026] This instance will expire in 7 days.",
                "(1785000060)[Sat Aug 1 20:01:00 2026] YOU hit a sentinel for 100 crushing damage.",
            ]);
            var seed = ZoneLookbehind.FindLastZoneSeed(path, new FileInfo(path).Length);
            Assert.NotNull(seed);
            Assert.Equal("Trials of the Awakened", seed.Zone);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785000000).AddDays(7), seed.InstanceExpiry);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Opening_A_V2_Database_Migrates_In_Place()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hist-v2-{Guid.NewGuid():N}.db");
        try
        {
            // Hand-build the v2 shape: no zone_instance_expiry column.
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE encounters (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        source_id TEXT NOT NULL, owner TEXT NOT NULL,
                        zone TEXT NOT NULL, title TEXT NOT NULL,
                        start_ts INTEGER NOT NULL, end_ts INTEGER NOT NULL,
                        duration_s REAL NOT NULL, success INTEGER NOT NULL,
                        damage INTEGER NOT NULL, correlation_id TEXT,
                        saved_at INTEGER NOT NULL, is_boss INTEGER NOT NULL DEFAULT 0
                    );
                    INSERT INTO encounters (source_id, owner, zone, title, start_ts, end_ts, duration_s, success, damage, correlation_id, saved_at, is_boss)
                    VALUES ('log', 'Sofja', 'Old Zone', 'Old Boss', 1785000000, 1785000100, 100, 2, 12345, NULL, 1785000200, 1);
                    PRAGMA user_version = 2;
                    """;
                cmd.ExecuteNonQuery();
            }
            // Opening with the new store migrates; the old row reads back
            // with a null expiry and everything still works.
            using var store = new History.HistoryStore(path);
            var summary = Assert.Single(store.SearchEncounters(
                since: DateTimeOffset.FromUnixTimeSeconds(0), bossOnly: true, limit: 10));
            Assert.Equal("Old Boss", summary.Title);
            Assert.Null(summary.ZoneInstanceExpiry);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void History_Roundtrips_The_Instance_Expiry_And_Migrates_Old_Rows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hist-{Guid.NewGuid():N}.db");
        try
        {
            long id;
            using (var store = new History.HistoryStore(path))
            {
                var engine = new ParserEngine("log", "Sofja");
                var processor = new LogLineProcessor(engine);
                var t = T0.ToUnixTimeSeconds();
                Feed(processor, t, "You have entered Trials of the Awakened.");
                Feed(processor, t, "This instance will expire in 7 days.");
                Feed(processor, t + 60, "YOU hit Palace Overseer for 5,000 piercing damage.");
                engine.EndCombat();
                id = store.SaveEncounter(engine.History[^1]);
            }
            using (var store = new History.HistoryStore(path))
            {
                var summary = Assert.Single(store.SearchEncounters(
                    since: DateTimeOffset.FromUnixTimeSeconds(0), bossOnly: true, limit: 10));
                Assert.Equal(T0.AddDays(7), summary.ZoneInstanceExpiry);
                var restored = store.RestoreEncounter(summary);
                Assert.Equal(T0.AddDays(7), restored.ZoneInstanceExpiry);
            }
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
