using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Correlation;
using EQ2Parser.Core.Engine;

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
    public HistoryService History { get; } = new();
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

    public LogSource Add(string path, bool parseFromStart)
    {
        var source = new LogSource(
            path, parseFromStart, Sync,
            new EngineOptions { IdleEndSeconds = Settings.IdleEndSeconds },
            TimeSpan.FromMilliseconds(Settings.PollMilliseconds),
            Triggers.CreateEngine(LogSource.DeriveOwner(path)),
            SpellTimers.Service);
        lock (Sync)
        {
            Correlator.Attach(source.Engine);
            source.Engine.EncounterEnded += History.QueueSave;
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
            // "Parse existing" is a one-time backfill at add time. On
            // restart the archive already holds that history — re-chewing
            // the log would duplicate every fight — so saved sources always
            // resume as live tails.
            if (System.IO.File.Exists(saved.Path))
                Add(saved.Path, parseFromStart: false);
        }
    }

    public void PersistSources()
    {
        Settings = Settings with
        {
            Sources = [.. Sources.Select(s => new SourceSetting(s.Path, s.ParseFromStart))],
        };
        Settings.Save();
    }

    public void Dispose()
    {
        foreach (var source in Sources)
            source.Dispose();
        History.Dispose();
        Audio.Dispose();
    }
}
