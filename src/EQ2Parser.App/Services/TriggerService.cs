using System.IO;
using System.Text.Json;
using EQ2Parser.Core.Persistence;
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
    double CooldownSeconds,
    string Zone = "");

/// <summary>
/// The app-side owner of the trigger system: the persisted definition list
/// (%LocalAppData%\EQ2Parser\triggers.json), one Core TriggerEngine per log
/// source (the engine needs the owner name for the YOU-group rule), and the
/// audio actions the Core engine decides on but never performs — beep, WAV,
/// TTS. Definition edits fan out to every live engine immediately.
///
/// Locking: <c>_gate</c> guards THIS service's lists only. Live engines are
/// matched by the log pump threads under the manager's global sync, so all
/// engine mutation happens under <c>_engineSync</c> (the same object) —
/// mutating them under _gate raced live matching. The two locks are never
/// nested; disk writes happen outside both.
/// </summary>
public sealed class TriggerService
{
    private readonly object _gate = new();
    private readonly object _engineSync;
    private readonly List<Trigger> _definitions = [];
    private readonly List<TriggerEngine> _engines = [];
    private readonly AlertAudioService _audio;

    /// <summary>Raised on a background thread each time any source's engine
    /// fires a trigger — the Triggers page shows a recent-fires feed off it.</summary>
    public event Action<TriggerFired>? AlertFired;

    /// <summary>Raised after any definition change — pages rebuild off this
    /// so edits from any window show everywhere.</summary>
    public event Action? DefinitionsChanged;

    public TriggerService(AlertAudioService audio, object engineSync)
    {
        _audio = audio;
        _engineSync = engineSync;
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
        Trigger[] defs;
        lock (_gate)
        {
            defs = [.. _definitions];
        }
        // Not yet registered or attached to a pump — fill lock-free, then
        // register so edits fan out to it from here on.
        engine.AddOrUpdateMany(defs);
        lock (_gate)
        {
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
        TriggerEngine[] engines;
        lock (_gate)
        {
            if (replaceKey is not null && replaceKey != trigger.Key)
                _definitions.RemoveAll(t => t.Key == replaceKey);
            var index = _definitions.FindIndex(t => t.Key == trigger.Key);
            if (index >= 0)
                _definitions[index] = trigger;
            else
                _definitions.Add(trigger);
            engines = [.. _engines];
        }
        lock (_engineSync)
        {
            foreach (var engine in engines)
            {
                if (replaceKey is not null && replaceKey != trigger.Key)
                    engine.Remove(replaceKey);
                engine.AddOrUpdate(trigger);
            }
        }
        Save();
        DefinitionsChanged?.Invoke();
    }

    /// <summary>Bulk upsert (imports): one save and one fan-out for the
    /// whole batch instead of per-trigger work.</summary>
    public int AddOrUpdateMany(IReadOnlyCollection<Trigger> triggers)
    {
        if (triggers.Count == 0)
            return 0;
        TriggerEngine[] engines;
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
            engines = [.. _engines];
        }
        lock (_engineSync)
        {
            foreach (var engine in engines)
                engine.AddOrUpdateMany(triggers);
        }
        Save();
        DefinitionsChanged?.Invoke();
        return triggers.Count;
    }

    public bool Remove(string key)
    {
        bool removed;
        TriggerEngine[] engines = [];
        lock (_gate)
        {
            removed = _definitions.RemoveAll(t => t.Key == key) > 0;
            if (removed)
                engines = [.. _engines];
        }
        if (!removed)
            return false;
        lock (_engineSync)
        {
            foreach (var engine in engines)
                engine.Remove(key);
        }
        Save();
        DefinitionsChanged?.Invoke();
        return true;
    }

    /// <summary>Raised when a LEXICON trigger's enable flips — the sync
    /// service persists these as overrides (lexicon rows never enter
    /// triggers.json).</summary>
    public event Action<string, bool>? LexiconEnabledChanged;

    public void SetEnabled(string key, bool enabled)
    {
        bool lexicon;
        Trigger updated;
        TriggerEngine[] engines;
        lock (_gate)
        {
            var index = _definitions.FindIndex(t => t.Key == key);
            if (index < 0 || _definitions[index].Enabled == enabled)
                return;
            updated = _definitions[index].WithEnabled(enabled);
            _definitions[index] = updated;
            lexicon = updated.Source.Length > 0;
            engines = [.. _engines];
        }
        lock (_engineSync)
        {
            foreach (var engine in engines)
                engine.AddOrUpdate(updated);
        }
        if (lexicon)
            LexiconEnabledChanged?.Invoke(key, enabled);
        else
            Save();
        // No DefinitionsChanged: enable flips patch rows in place — a full
        // rebuild would reset the list's scroll on every checkbox click.
    }

    /// <summary>Replace the synced Lexicon set wholesale: old lexicon
    /// triggers out of the list and every engine, the new pack's in —
    /// except where the user's OWN trigger has the same identity (their
    /// copy/fork wins, compared Ordinal like every other key test).
    /// Enable/disable overrides re-apply in BOTH directions.</summary>
    public void ApplyLexicon(IReadOnlyCollection<Trigger> triggers, IReadOnlySet<string> disabledKeys, IReadOnlySet<string> enabledKeys)
    {
        List<string> removedKeys = [];
        List<Trigger> added;
        TriggerEngine[] engines;
        lock (_gate)
        {
            foreach (var old in _definitions.Where(t => t.Source.Length > 0).ToList())
            {
                _definitions.Remove(old);
                removedKeys.Add(old.Key);
            }
            var customKeys = _definitions.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
            added = LexiconMerge.Plan(triggers, customKeys, disabledKeys, enabledKeys,
                static t => t.Key, static t => t.Enabled, static (t, e) => t.WithEnabled(e));
            _definitions.AddRange(added);
            engines = [.. _engines];
        }
        lock (_engineSync)
        {
            foreach (var engine in engines)
            {
                engine.RemoveMany(removedKeys);
                engine.AddOrUpdateMany(added);
            }
        }
        DefinitionsChanged?.Invoke();
    }

    /// <summary>Cross-source dedupe: every live log in the same raid sees
    /// the same emote line, and every source's engine fires — the player
    /// must hear it ONCE. Keyed by trigger + matched text so two DIFFERENT
    /// matches of one trigger ("cure Alice" vs "cure Bob") both announce.
    /// The per-trigger AudioCooldown lives per-engine and can't help here.
    /// (Trigger-started timers dedupe separately in SpellTimerService.)</summary>
    private readonly Dictionary<string, long> _recentFireMs = new(StringComparer.Ordinal);

    private const long CrossSourceDedupeMs = 2000;

    private void HandleFired(TriggerFired fired)
    {
        // Runs under the manager's sync lock on the log pump thread — every
        // audio call hands off to the audio service's own tasks/queue, so
        // nothing here blocks the parse.
        var dedupeKey = $"{fired.Trigger.Key}{fired.Match.Value}";
        var now = Environment.TickCount64;
        if (_recentFireMs.TryGetValue(dedupeKey, out var last) && now - last < CrossSourceDedupeMs)
            return; // a mirror log already announced this exact event
        if (_recentFireMs.Count > 256)
        {
            // Bound the map — drop anything already outside the window.
            foreach (var stale in _recentFireMs.Where(kv => now - kv.Value >= CrossSourceDedupeMs).Select(kv => kv.Key).ToList())
                _recentFireMs.Remove(stale);
        }
        _recentFireMs[dedupeKey] = now;

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

    private void Load()
    {
        var settings = PersistedJsonFile.Load<List<TriggerSetting>>(FilePath, static () => []);
        foreach (var s in settings)
        {
            try
            {
                _definitions.Add(new Trigger(s.Regex, s.Category, s.Zone)
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

    private void Save()
    {
        // Snapshot under the gate, write OUTSIDE it — callers invoke Save
        // after releasing their locks so disk I/O never blocks matching.
        List<TriggerSetting> settings;
        lock (_gate)
        {
            // The user's own triggers only — the Lexicon set is a synced
            // mirror, re-materialised from the pack cache on startup.
            settings = [.. _definitions.Where(t => t.Source.Length == 0).Select(t => new TriggerSetting(
                t.RegexText, t.Category, t.Enabled, t.RestrictToCategoryZone,
                (int)t.SoundType, t.SoundData, t.StartsTimer, t.TimerName,
                t.AudioCooldown.TotalSeconds, t.Zone))];
        }
        try
        {
            PersistedJsonFile.Save(FilePath, settings);
        }
        catch (Exception)
        {
            // Persistence failure degrades to in-memory triggers.
        }
    }
}
