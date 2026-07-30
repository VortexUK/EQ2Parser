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
    public AppSettings Settings { get; set; } = AppSettings.Load();

    /// <summary>Raised (on a background thread) whenever a correlated fight
    /// is created or merged — history views resync on it.</summary>
    public event Action? HistoryChanged;

    public SourceManager()
    {
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
            TimeSpan.FromMilliseconds(Settings.PollMilliseconds));
        lock (Sync)
        {
            Correlator.Attach(source.Engine);
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
        source.Dispose();
    }

    public void RestoreFromSettings()
    {
        foreach (var saved in Settings.Sources)
        {
            if (System.IO.File.Exists(saved.Path))
                Add(saved.Path, saved.ParseFromStart);
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
    }
}
