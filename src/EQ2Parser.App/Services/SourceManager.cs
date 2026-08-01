using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Correlation;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.Services;

/// <summary>
/// The app's parsing backbone: the set of tailed log sources, the shared
/// encounter correlator, and the shared class/pet classifier. One global
/// sync object serializes every engine mutation and correlator callback;
/// UI snapshots take the same lock briefly.
/// </summary>
public sealed class SourceManager : IDisposable
{
    private readonly List<LogSource> _sources = [];

    public object Sync { get; } = new();
    public EncounterCorrelator Correlator { get; } = new();
    public CombatantClassifier Classifier { get; } = new(new ClassIdentifier(SpellClassMap.LoadEmbedded()));
    public AlertAudioService Audio { get; }
    public TriggerService Triggers { get; }
    public TimerService SpellTimers { get; }
    public LexiconSyncService Lexicon { get; }
    public StatusCalloutMonitor Callouts { get; } = new();

    /// <summary>A mass-detriment callout was announced (already spoken) —
    /// the Notifications overlay toasts it too.</summary>
    public event Action<string>? CalloutAnnounced;
    public HistoryService History { get; } = new();
    public UpdateService Updates { get; } = new();
    public AppSettings Settings { get; set; } = AppSettings.Load();

    /// <summary>Raised (on a background thread) whenever a correlated fight
    /// is created or merged — history views resync on it.</summary>
    public event Action? HistoryChanged;

    public SourceManager()
    {
        Audio = new AlertAudioService
        {
            Volume = Settings.AlertVolume,
            SpeakingRate = Settings.TtsRate,
            VoiceId = Settings.TtsVoiceId,
        };
        Triggers = new TriggerService(Audio);
        SpellTimers = new TimerService(Audio, Sync);
        Lexicon = new LexiconSyncService(Triggers, SpellTimers, Settings.LexiconBaseUrl);
        Callouts.MinVictims = Settings.CalloutMinPlayers;
        Callouts.Cooldown = TimeSpan.FromSeconds(Settings.CalloutCooldownSeconds);
        Callouts.Callout += (effect, count) =>
        {
            if (!Settings.CalloutsEnabled)
                return;
            var word = Vocabulary.CalloutWord(effect);
            var text = count == 1 ? $"1 player {word}" : $"{count} players {word}";
            Audio.Speak(text);
            CalloutAnnounced?.Invoke(text);
        };
        Correlator.Created += _ => HistoryChanged?.Invoke();
        Correlator.Merged += _ => HistoryChanged?.Invoke();
    }

    public IReadOnlyList<LogSource> Sources
    {
        get
        {
            lock (Sync)
            {
                return [.. _sources];
            }
        }
    }

    /// <summary>True while any source is chewing a big backlog (wipe +
    /// re-import, first parse-from-start). The meters pause and the tree
    /// throttles while this holds — replaying history through the live UI
    /// was slowing imports to a crawl.</summary>
    public bool ImportBusy
    {
        get
        {
            foreach (var source in Sources)
            {
                if (source.PendingBytes > 1_000_000)
                    return true;
            }
            return false;
        }
    }

    public LogSource Add(string path, bool parseFromStart, long? startOffset = null, bool autoDiscovered = false)
    {
        var source = new LogSource(
            path, parseFromStart, Sync,
            new EngineOptions { IdleEndSeconds = Settings.IdleEndSeconds },
            TimeSpan.FromMilliseconds(Settings.PollMilliseconds),
            Triggers.CreateEngine(LogSource.DeriveOwner(path)),
            SpellTimers.Service,
            startOffset)
        {
            AutoDiscovered = autoDiscovered,
        };
        lock (Sync)
        {
            Correlator.Attach(source.Engine);
            source.Engine.EncounterEnded += History.QueueSave;
            source.Processor.StatusApplied += Callouts.OnStatusApplied;
            _sources.Add(source);
        }
        return source;
    }

    public void Remove(LogSource source)
    {
        lock (Sync)
        {
            _sources.Remove(source);
        }
        if (source.TriggerEngine is { } engine)
            Triggers.RemoveEngine(engine);
        source.Dispose();
    }

    /// <summary>Load past sessions' fights into the correlator — called at
    /// startup BEFORE the log tails start, so live fights land on top.</summary>
    public void RestoreHistory()
    {
        lock (Sync)
        {
            History.RestoreInto(Correlator, Settings.HistoryBossDays, Settings.HistoryTrashDays);
        }
    }

    public void RestoreFromSettings()
    {
        foreach (var saved in Settings.Sources)
        {
            // "Parse existing" is a one-time backfill at add time — the
            // archive holds that history. Restarts resume from the last
            // consumed byte, catching up whatever was written while the
            // app was closed (alerts stay silent for old lines; the dedup
            // guard keeps the archive clean). No saved position (older
            // settings) = tail from the end. Auto-discovered sources are
            // the folder watcher's to re-add — only when active again.
            if (!saved.AutoDiscovered && System.IO.File.Exists(saved.Path))
                Add(saved.Path, parseFromStart: false, saved.LastPosition);
        }
    }

    // ── Folder watch: track any log that becomes active ─────────────────────

    private readonly CancellationTokenSource _watchCts = new();
    private Task? _watchTask;

    public IReadOnlyList<string> WatchedFolders => Settings.WatchedFolders;

    /// <summary>Begin scanning watched folders — called AFTER
    /// RestoreFromSettings so manual sources win any race for a path.</summary>
    public void StartFolderWatch() =>
        _watchTask ??= Task.Run(() => WatchLoopAsync(_watchCts.Token));

    public void AddWatchedFolder(string folder)
    {
        if (Settings.WatchedFolders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
            return;
        Settings = Settings with { WatchedFolders = [.. Settings.WatchedFolders, folder] };
        Settings.Save();
    }

    /// <summary>Stop watching a folder and detach the sources it added.</summary>
    public void RemoveWatchedFolder(string folder)
    {
        Settings = Settings with
        {
            WatchedFolders = [.. Settings.WatchedFolders.Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))],
        };
        Settings.Save();
        foreach (var source in Sources)
        {
            if (source.AutoDiscovered
                && source.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                Remove(source);
        }
    }

    /// <summary>Every ~2 s: any eq2log_*.txt under a watched folder (all
    /// server subfolders) that GROWS becomes a live source. A log with a
    /// saved position resumes there (catch-up); a never-seen log baselines
    /// at its current length and attaches only when it grows past it — so
    /// pointing at the logs root never chews a giant dormant file.</summary>
    private async Task WatchLoopAsync(CancellationToken ct)
    {
        Dictionary<string, long> baselines = new(StringComparer.OrdinalIgnoreCase);
        while (!ct.IsCancellationRequested)
        {
            foreach (var folder in Settings.WatchedFolders)
            {
                IEnumerable<string> files;
                try
                {
                    if (!System.IO.Directory.Exists(folder))
                        continue;
                    files = System.IO.Directory.EnumerateFiles(folder, "eq2log_*.txt", System.IO.SearchOption.AllDirectories);
                }
                catch (Exception)
                {
                    continue; // unreadable folder this pass — retry next
                }
                foreach (var file in files)
                {
                    try
                    {
                        if (Sources.Any(s => string.Equals(s.Path, file, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var length = new System.IO.FileInfo(file).Length;
                        var saved = Settings.Sources.FirstOrDefault(s =>
                            string.Equals(s.Path, file, StringComparison.OrdinalIgnoreCase));
                        if (saved?.LastPosition is { } position)
                        {
                            // Known log: catch up when it grew; a shrink is
                            // rotation (all-new content, read from the top).
                            if (length != position)
                                Add(file, parseFromStart: false, Math.Min(position, length), autoDiscovered: true);
                        }
                        else if (!baselines.TryGetValue(file, out var baseline))
                        {
                            baselines[file] = length;
                        }
                        else if (length > baseline)
                        {
                            baselines.Remove(file);
                            Add(file, parseFromStart: false, baseline, autoDiscovered: true);
                        }
                        else if (length < baseline)
                        {
                            baselines[file] = length; // rotated while dormant
                        }
                    }
                    catch (Exception)
                    {
                        // One unreadable file never stops the sweep.
                    }
                }
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void PersistSources()
    {
        // Live sources with fresh positions, plus dormant auto-discovered
        // entries from earlier sessions — their resume positions must
        // survive sessions where the log never woke up.
        List<SourceSetting> persisted =
            [.. Sources.Select(s => new SourceSetting(s.Path, s.ParseFromStart, s.LastPosition, s.AutoDiscovered))];
        var livePaths = new HashSet<string>(persisted.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
        persisted.AddRange(Settings.Sources.Where(s => s.AutoDiscovered && !livePaths.Contains(s.Path)));
        Settings = Settings with { Sources = persisted };
        Settings.Save();
    }

    public void Dispose()
    {
        _watchCts.Cancel();
        foreach (var source in Sources)
            source.Dispose();
        History.Dispose();
        Audio.Dispose();
        _watchCts.Dispose();
    }
}
