using System.Collections.Concurrent;
using System.Text;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.History;
using EQ2Parser.Core.Logs;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// The second clock: arrival stamps flow tail-reader → LogLine → Swing →
/// timers → history, while ACT/site-compatible stat math stays on the
/// whole-second log timestamps.
/// </summary>
public sealed class ArrivalTimeTests : IDisposable
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);
    private readonly string _dir = Directory.CreateTempSubdirectory("eq2parser-arrival-").FullName;
    private readonly CancellationTokenSource _cts = new();

    public void Dispose()
    {
        _cts.Cancel();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Reader_Stamps_Batches_With_The_Injected_Clock()
    {
        var path = Path.Combine(_dir, "eq2log_Test.txt");
        var now = T0;
        var reader = new LogTailReader(path, new LogTailOptions
        {
            StartAtEnd = false,
            PollInterval = TimeSpan.FromMilliseconds(15),
            Clock = () => now,
        });
        var lines = new ConcurrentQueue<TailedLine>();
        _ = Task.Run(async () =>
        {
            await foreach (var line in reader.ReadLinesAsync(_cts.Token))
                lines.Enqueue(line);
        });

        // Two lines in ONE write → one batch → same stamp.
        File.AppendAllText(path, "(1)[s] one\n(1)[s] two\n", Encoding.UTF8);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (lines.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(2, lines.Count);
        var batch = lines.ToArray();
        Assert.Equal(batch[0].ObservedAt, batch[1].ObservedAt);
        Assert.Equal(T0, batch[0].ObservedAt);

        // A later write under an advanced clock gets the new stamp.
        now = T0.AddMilliseconds(730);
        File.AppendAllText(path, "(2)[s] three\n", Encoding.UTF8);
        while (lines.Count < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(T0.AddMilliseconds(730), lines.Last().ObservedAt);
    }

    [Fact]
    public void Swings_Carry_ObservedAt_But_Stats_Use_Log_Time()
    {
        var engine = new ParserEngine("log-a", "Menludiir");
        var processor = new LogLineProcessor(engine);

        Assert.True(LogLine.TryParse($"({T0.ToUnixTimeSeconds()})[s] YOUR Smite hits a gnoll for 500 divine damage.", out var line));
        processor.Process(line with { ObservedAt = T0.AddMilliseconds(340) });
        engine.EndCombat();

        var encounter = engine.History[^1];
        var swing = encounter.Combatants["MENLUDIIR"]
            .OutgoingBuckets[BucketConfig.OutgoingDamage].All.Swings.Single();
        Assert.Equal(T0.AddMilliseconds(340), swing.ObservedAt);
        // The stat clock is untouched: whole-second log time.
        Assert.Equal(T0, swing.Time);
        Assert.Equal(T0, encounter.StartTime);
    }

    [Fact]
    public void Timers_Anchor_To_Arrival_When_Live()
    {
        var engine = new ParserEngine("log-a", "Menludiir");
        var timers = new SpellTimerService();
        timers.AddOrUpdateDefinition(new TimerDefinition { Name = "Smite", DurationSeconds = 30 });
        var processor = new LogLineProcessor(engine, timers: timers);

        var starts = new List<ActiveTimer>();
        timers.TimerStarted += (_, t) => starts.Add(t);

        Assert.True(LogLine.TryParse($"({T0.ToUnixTimeSeconds()})[s] YOUR Smite hits a gnoll for 500 divine damage.", out var line));
        processor.Process(line with { ObservedAt = T0.AddMilliseconds(340) });

        Assert.Equal(T0.AddMilliseconds(340), Assert.Single(starts).Start);

        // Import mode (no arrival stamp) anchors to log time.
        var engine2 = new ParserEngine("log-b", "Menludiir");
        var timers2 = new SpellTimerService();
        timers2.AddOrUpdateDefinition(new TimerDefinition { Name = "Smite", DurationSeconds = 30 });
        var processor2 = new LogLineProcessor(engine2, timers: timers2);
        var starts2 = new List<ActiveTimer>();
        timers2.TimerStarted += (_, t) => starts2.Add(t);
        processor2.Process(line);
        Assert.Equal(T0, Assert.Single(starts2).Start);
    }

    [Fact]
    public void History_RoundTrips_ObservedAt()
    {
        var engine = new ParserEngine("log-a", "Menludiir");
        Assert.True(engine.SetEncounter(T0, "Menludiir", "a gnoll"));
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menludiir", "Strike", 100, T0, "a gnoll", "crushing",
            extra: null, observedAt: T0.AddMilliseconds(120));
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menludiir", "Strike", 100, T0.AddSeconds(1), "a gnoll", "crushing");
        engine.EndCombat();

        using var store = new HistoryStore(Path.Combine(_dir, "history.db"));
        var id = store.SaveEncounter(engine.History[^1]);
        var swings = store.LoadSwings(id);
        Assert.Equal(T0.AddMilliseconds(120), swings[0].ObservedAt);
        Assert.Null(swings[1].ObservedAt);
    }
}
