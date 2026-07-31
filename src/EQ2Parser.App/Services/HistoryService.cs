using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;
using EQ2Parser.Core.History;

namespace EQ2Parser.App.Services;

/// <summary>
/// Cross-session parse history: every finished encounter queues to a
/// background writer (the log pump never blocks on SQLite), the last
/// sessions' fights restore into the correlator at startup — re-merging
/// multi-log raids exactly like live parsing — and deleting a fight in the
/// UI deletes its stored rows so it stays gone. Retention prunes on start.
/// </summary>
public sealed class HistoryService : IDisposable
{
    /// <summary>Restore cap — enough for several raid nights without a
    /// startup stall; older fights stay on disk until retention.</summary>
    private const int MaxRestoredFights = 150;

    private readonly HistoryStore _store;
    private readonly object _gate = new();
    private readonly Channel<Encounter> _saves = Channel.CreateUnbounded<Encounter>();
    private readonly Task _writer;
    private readonly ConditionalWeakTable<Encounter, StrongBox<long>> _storedIds = [];

    public HistoryService()
    {
        Directory.CreateDirectory(AppSettings.Directory);
        _store = new HistoryStore(Path.Combine(AppSettings.Directory, "history.db"));
        _writer = Task.Run(async () =>
        {
            await foreach (var encounter in _saves.Reader.ReadAllAsync())
            {
                try
                {
                    long id;
                    lock (_gate)
                    {
                        id = _store.SaveEncounter(encounter);
                    }
                    _storedIds.Add(encounter, new StrongBox<long>(id));
                }
                catch (Exception)
                {
                    // A failed save loses one fight, never the app.
                }
            }
        });
    }

    /// <summary>Engine EncounterEnded handler — snapshot is final by now,
    /// so the actual write happens off-thread.</summary>
    public void QueueSave(Encounter encounter)
    {
        if (encounter.Title == Encounter.PlaceholderTitle)
            return; // engine scraps these; never persist them
        _saves.Writer.TryWrite(encounter);
    }

    /// <summary>Startup: prune to retention, then replay the most recent
    /// fights (oldest first) through the correlator. Returns fights loaded.</summary>
    public int RestoreInto(EncounterCorrelator correlator, int retentionDays)
    {
        lock (_gate)
        {
            _store.PruneBefore(DateTimeOffset.Now.AddDays(-Math.Max(1, retentionDays)));
            var summaries = _store.QueryEncounters(limit: MaxRestoredFights);
            foreach (var summary in summaries)
                correlator.RegisterOwner(summary.Owner);
            foreach (var summary in summaries.Reverse())
            {
                var encounter = _store.RestoreEncounter(summary);
                _storedIds.Add(encounter, new StrongBox<long>(summary.Id));
                correlator.Accept(encounter);
            }
            return summaries.Count;
        }
    }

    /// <summary>UI deletion of a fight: remove every source encounter's
    /// stored rows so it doesn't resurrect next start.</summary>
    public void DeleteFight(CorrelatedEncounter fight)
    {
        foreach (var encounter in fight.Sources)
        {
            if (_storedIds.TryGetValue(encounter, out var box))
            {
                lock (_gate)
                {
                    _store.DeleteEncounter(box.Value);
                }
            }
        }
    }

    public void Dispose()
    {
        _saves.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Shutdown path — nothing to report.
        }
        lock (_gate)
        {
            _store.Dispose();
        }
    }
}
