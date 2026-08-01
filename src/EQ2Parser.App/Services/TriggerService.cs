using System.IO;
using System.Text.Json;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.Services;

/// <summary>JSON shape for one persisted trigger — mirrors the Trigger
/// fields the ACT share format round-trips, plus our per-trigger cooldown.</summary>
public sealed record TriggerSetting(
    string Regex,
    string Category,
    bool Enabled,
    bool RestrictToZone,
    int SoundType,
    string SoundData,
    bool StartsTimer,
    string TimerName,
    double CooldownSeconds);

/// <summary>
/// The app-side owner of the trigger system: the persisted definition list
/// (%LocalAppData%\EQ2Parser\triggers.json), one Core TriggerEngine per log
/// source (the engine needs the owner name for the YOU-group rule), and the
/// audio actions the Core engine decides on but never performs — beep, WAV,
/// TTS. Definition edits fan out to every live engine immediately.
/// </summary>
public sealed class TriggerService
{
    private readonly object _gate = new();
    private readonly List<Trigger> _definitions = [];
    private readonly List<TriggerEngine> _engines = [];
    private readonly AlertAudioService _audio;

    /// <summary>Raised on a background thread each time any source's engine
    /// fires a trigger — the Triggers page shows a recent-fires feed off it.</summary>
    public event Action<TriggerFired>? AlertFired;

    /// <summary>Raised after any definition change — pages rebuild off this
    /// so edits from any window show everywhere.</summary>
    public event Action? DefinitionsChanged;

    public TriggerService(AlertAudioService audio)
    {
        _audio = audio;
        Load();
    }

    public IReadOnlyList<Trigger> Definitions
    {
        get
        {
            lock (_gate)
            {
                return [.. _definitions];
            }
        }
    }

    /// <summary>New engine for a log source, pre-loaded with every
    /// definition and wired into the audio/alert pipeline.</summary>
    public TriggerEngine CreateEngine(string ownerName)
    {
        var engine = new TriggerEngine(ownerName);
        engine.Fired += HandleFired;
        lock (_gate)
        {
            foreach (var trigger in _definitions)
                engine.AddOrUpdate(trigger);
            _engines.Add(engine);
        }
        return engine;
    }

    public void RemoveEngine(TriggerEngine engine)
    {
        engine.Fired -= HandleFired;
        lock (_gate)
        {
            _engines.Remove(engine);
        }
    }

    /// <summary>Add or update a definition. <paramref name="replaceKey"/> is
    /// the trigger's previous identity when an edit changed regex/category
    /// (the key), so the old row is removed everywhere first.</summary>
    public void AddOrUpdate(Trigger trigger, string? replaceKey = null)
    {
        lock (_gate)
        {
            if (replaceKey is not null && replaceKey != trigger.Key)
            {
                _definitions.RemoveAll(t => t.Key == replaceKey);
                foreach (var engine in _engines)
                    engine.Remove(replaceKey);
            }
            var index = _definitions.FindIndex(t => t.Key == trigger.Key);
            if (index >= 0)
                _definitions[index] = trigger;
            else
                _definitions.Add(trigger);
            foreach (var engine in _engines)
                engine.AddOrUpdate(trigger);
            Save();
        }
        DefinitionsChanged?.Invoke();
    }

    /// <summary>Bulk upsert (imports): one save and one fan-out for the
    /// whole batch instead of per-trigger work.</summary>
    public int AddOrUpdateMany(IReadOnlyCollection<Trigger> triggers)
    {
        if (triggers.Count == 0)
            return 0;
        lock (_gate)
        {
            foreach (var trigger in triggers)
            {
                var index = _definitions.FindIndex(t => t.Key == trigger.Key);
                if (index >= 0)
                    _definitions[index] = trigger;
                else
                    _definitions.Add(trigger);
            }
            foreach (var engine in _engines)
            {
                foreach (var trigger in triggers)
                    engine.AddOrUpdate(trigger);
            }
            Save();
        }
        DefinitionsChanged?.Invoke();
        return triggers.Count;
    }

    public bool Remove(string key)
    {
        bool removed;
        lock (_gate)
        {
            removed = _definitions.RemoveAll(t => t.Key == key) > 0;
            if (removed)
            {
                foreach (var engine in _engines)
                    engine.Remove(key);
                Save();
            }
        }
        if (removed)
            DefinitionsChanged?.Invoke();
        return removed;
    }

    public void SetEnabled(string key, bool enabled)
    {
        Trigger? updated = null;
        lock (_gate)
        {
            var index = _definitions.FindIndex(t => t.Key == key);
            if (index < 0 || _definitions[index].Enabled == enabled)
                return;
            updated = CloneWith(_definitions[index], enabled);
            _definitions[index] = updated;
            foreach (var engine in _engines)
                engine.AddOrUpdate(updated);
            Save();
        }
        DefinitionsChanged?.Invoke();
    }

    private static Trigger CloneWith(Trigger t, bool enabled) => new(t.RegexText, t.Category)
    {
        Enabled = enabled,
        RestrictToCategoryZone = t.RestrictToCategoryZone,
        SoundType = t.SoundType,
        SoundData = t.SoundData,
        StartsTimer = t.StartsTimer,
        TimerName = t.TimerName,
        AudioCooldown = t.AudioCooldown,
    };

    private void HandleFired(TriggerFired fired)
    {
        // Runs under the manager's sync lock on the log pump thread — every
        // audio call hands off to the audio service's own tasks/queue, so
        // nothing here blocks the parse.
        if (fired.PlayBeep)
            _audio.PlayChime();
        else if (fired.WavFile is { Length: > 0 } wav)
            _audio.PlayFile(wav);
        else if (fired.TtsText is { Length: > 0 } text)
            _audio.Speak(text);
        AlertFired?.Invoke(fired);
    }

    // ---- persistence ----

    private static string FilePath => Path.Combine(AppSettings.Directory, "triggers.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;
            var settings = JsonSerializer.Deserialize<List<TriggerSetting>>(File.ReadAllText(FilePath)) ?? [];
            foreach (var s in settings)
            {
                try
                {
                    _definitions.Add(new Trigger(s.Regex, s.Category)
                    {
                        Enabled = s.Enabled,
                        RestrictToCategoryZone = s.RestrictToZone,
                        SoundType = Enum.IsDefined((TriggerSound)s.SoundType) ? (TriggerSound)s.SoundType : TriggerSound.None,
                        SoundData = s.SoundData,
                        StartsTimer = s.StartsTimer,
                        TimerName = s.TimerName,
                        AudioCooldown = TimeSpan.FromSeconds(Math.Clamp(s.CooldownSeconds, 0, 3600)),
                    });
                }
                catch (ArgumentException)
                {
                    // A regex that no longer compiles is dropped, not fatal.
                }
            }
        }
        catch (Exception)
        {
            // Corrupt trigger file never blocks startup.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            List<TriggerSetting> settings = [.. _definitions.Select(t => new TriggerSetting(
                t.RegexText, t.Category, t.Enabled, t.RestrictToCategoryZone,
                (int)t.SoundType, t.SoundData, t.StartsTimer, t.TimerName,
                t.AudioCooldown.TotalSeconds))];
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception)
        {
            // Persistence failure degrades to in-memory triggers.
        }
    }
}
