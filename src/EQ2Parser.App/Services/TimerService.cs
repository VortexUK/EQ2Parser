using System.IO;
using System.Text.Json;
using EQ2Parser.Core.Persistence;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.Services;

/// <summary>One bar for the overlay / live preview: a snapshot of an
/// active timer at a moment in time.</summary>
public sealed record TimerBarSnapshot(
    string Name,
    string Combatant,
    double SecondsLeft,
    int DurationSeconds,
    int WarningSeconds,
    int FillColorArgb,
    bool Radial,
    string DamageType = "",
    string ControlEffect = "",
    /// <summary>Percent the timer was STRETCHED by recast debuffs
    /// (Traumatic Swipe): 50 for base×1.5. 0 = unmodified.</summary>
    int SwipedPercent = 0);

/// <summary>
/// The app-side owner of spell timers: ONE shared Core SpellTimerService
/// fed by every log source (its 2-second retrigger dedupe is what makes
/// multiple logs of the same raid produce one bar, not two), persisted
/// definitions (%LocalAppData%\EQ2Parser\timers.json), and the start/warning
/// sounds the Core engine signals but never plays. All mutations and reads
/// go under the manager's sync object — the same lock the log pumps hold
/// while notifying.
/// </summary>
public sealed class TimerService
{
    private readonly object _sync;
    private readonly AlertAudioService _audio;

    public SpellTimerService Service { get; } = new();

    /// <summary>Raised after any definition change — pages rebuild off
    /// this, so edits from any window (Timers page, Curation, future
    /// Lexicon sync) show everywhere.</summary>
    public event Action? DefinitionsChanged;

    public TimerService(AlertAudioService audio, object sync)
    {
        _audio = audio;
        _sync = sync;
        Service.TimerStarted += (frame, timer) =>
        {
            if (timer.IsMaster)
                PlaySound(frame.Definition, frame.Definition.StartSoundData, warning: false);
        };
        Service.WarningReached += (frame, _) => PlaySound(frame.Definition, frame.Definition.WarningSoundData, warning: true);
        Load();
    }

    /// <summary>A WAV/MP3 path, spoken text, or ACT's "tts" convention —
    /// "tts some words" speaks the words, a bare "tts" speaks the timer's
    /// name (plus "soon" for the warning), matching what 20 years of
    /// exported spell timers expect.</summary>
    private void PlaySound(TimerDefinition definition, string data, bool warning)
    {
        data = data.Trim();
        if (data.Length == 0)
            return;
        if (File.Exists(data))
        {
            _audio.PlayFile(data);
            return;
        }
        if (data.Equals("tts", StringComparison.OrdinalIgnoreCase))
            data = warning ? $"{definition.Name} soon" : definition.Name;
        else if (data.StartsWith("tts ", StringComparison.OrdinalIgnoreCase))
            data = data[4..].Trim();
        _audio.Speak(data);
    }

    public IReadOnlyList<TimerDefinition> Definitions
    {
        get
        {
            lock (_sync)
            {
                return [.. Service.Definitions];
            }
        }
    }

    public void AddOrUpdate(TimerDefinition definition, string? replaceKey = null)
    {
        lock (_sync)
        {
            if (replaceKey is not null && replaceKey != definition.Key)
                Service.RemoveDefinition(replaceKey);
            Service.AddOrUpdateDefinition(definition);
        }
        Save();
        DefinitionsChanged?.Invoke();
    }

    public void Remove(string key)
    {
        bool removed;
        lock (_sync)
        {
            removed = Service.RemoveDefinition(key);
        }
        if (!removed)
            return;
        Save();
        DefinitionsChanged?.Invoke();
    }

    /// <summary>Raised when a LEXICON definition's enable flips — the sync
    /// service persists these as overrides (lexicon rows never enter
    /// timers.json).</summary>
    public event Action<string, bool>? LexiconEnabledChanged;

    public void SetEnabled(string key, bool enabled)
    {
        bool lexicon;
        lock (_sync)
        {
            var current = Service.Definitions.FirstOrDefault(d => d.Key == key);
            if (current is null || current.Enabled == enabled)
                return;
            Service.AddOrUpdateDefinition(current with { Enabled = enabled });
            lexicon = current.Source.Length > 0;
        }
        if (lexicon)
            LexiconEnabledChanged?.Invoke(key, enabled);
        else
            Save();
        // No DefinitionsChanged: enable flips patch rows in place — a full
        // rebuild would reset the list's scroll on every checkbox click.
    }

    /// <summary>Replace the synced Lexicon set wholesale: every old
    /// lexicon-sourced definition goes, the new pack's come in — except
    /// where the user has their OWN definition with the same identity
    /// (their copy/fork wins; timer keys are pre-lowercased so Ordinal is
    /// exact). Enable/disable overrides re-apply in BOTH directions.</summary>
    public void ApplyLexicon(IReadOnlyCollection<TimerDefinition> definitions, IReadOnlySet<string> disabledKeys, IReadOnlySet<string> enabledKeys)
    {
        lock (_sync)
        {
            foreach (var key in Service.Definitions.Where(d => d.Source.Length > 0).Select(d => d.Key).ToList())
                Service.RemoveDefinition(key);
            var customKeys = Service.Definitions.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                if (customKeys.Contains(definition.Key))
                    continue;
                var enabled = !disabledKeys.Contains(definition.Key)
                    && (definition.Enabled || enabledKeys.Contains(definition.Key));
                Service.AddOrUpdateDefinition(definition with { Enabled = enabled });
            }
        }
        DefinitionsChanged?.Invoke();
    }

    /// <summary>The trigger-side half of a linked pair is useless without
    /// the timer-side half — ACT quietly auto-registers unknown spell
    /// timers, and so do we: a 30s default the user then tunes. Lives here
    /// so BOTH import paths run it (the Timers page's used to skip it).</summary>
    public int EnsureLinkedTimers(IEnumerable<Trigger> triggers)
    {
        var created = 0;
        foreach (var trigger in triggers)
        {
            if (!trigger.StartsTimer || trigger.TimerName.Length == 0
                || Definitions.Any(d => d.Name.Equals(trigger.TimerName, StringComparison.OrdinalIgnoreCase)))
                continue;
            AddOrUpdate(new TimerDefinition
            {
                Name = trigger.TimerName,
                Category = trigger.Category,
                Zone = trigger.Zone,
                // Trigger-started: the notify's attacker is whatever the
                // regex matched, so a category lock would strangle it.
                RestrictToCategory = false,
                WarningSoundData = "tts",
            });
            created++;
        }
        return created;
    }

    public int ImportMany(IReadOnlyCollection<TimerDefinition> definitions)
    {
        if (definitions.Count == 0)
            return 0;
        lock (_sync)
        {
            foreach (var definition in definitions)
                Service.AddOrUpdateDefinition(definition);
        }
        Save();
        DefinitionsChanged?.Invoke();
        return definitions.Count;
    }

    /// <summary>Advance the clock: raises warning/expiry events and prunes
    /// finished bars. Called from the UI tick.</summary>
    public void Tick(DateTimeOffset now)
    {
        lock (_sync)
        {
            Service.Tick(now);
        }
    }

    /// <summary>Start a timer by hand — the editor's Test button. The victim
    /// doubles as the definition's category so category-restricted timers
    /// still fire, and the definition's zone rides along so the RIGHT twin
    /// starts when the same mob+ability exists for several zones.</summary>
    public void StartTest(TimerDefinition definition)
    {
        lock (_sync)
        {
            Service.Notify("you", definition.Name, self: true, definition.Category, DateTimeOffset.Now, definition.Zone);
        }
    }

    /// <summary>Every active timer routed to the given panel (1 or 2) as an
    /// immutable bar snapshot, soonest expiry first. A timer can be
    /// sound-only (both panels off) — it still fires its events.</summary>
    public List<TimerBarSnapshot> Snapshot(DateTimeOffset now, int panel = 1)
    {
        List<TimerBarSnapshot> bars = [];
        lock (_sync)
        {
            foreach (var frame in Service.Frames)
            {
                if (!(panel == 2 ? frame.Definition.Panel2 : frame.Definition.Panel1))
                    continue;
                foreach (var timer in frame.Timers)
                {
                    // A swiped timer runs LONGER than its base — surface by
                    // how much, so the raid knows the recast they're staring
                    // at was stretched, not mis-curated.
                    var swiped = timer.BaseDurationSeconds > 0 && timer.DurationSeconds != timer.BaseDurationSeconds
                        ? (int)Math.Round(100.0 * (timer.DurationSeconds - timer.BaseDurationSeconds) / timer.BaseDurationSeconds)
                        : 0;
                    bars.Add(new TimerBarSnapshot(
                        frame.Definition.Name,
                        frame.Combatant,
                        timer.SecondsLeft(now),
                        timer.DurationSeconds,
                        frame.Definition.WarningSeconds,
                        frame.Definition.FillColorArgb,
                        frame.Definition.RadialDisplay,
                        frame.Definition.DamageType,
                        frame.Definition.ControlEffect,
                        swiped));
                }
            }
        }
        bars.Sort((a, b) => a.SecondsLeft.CompareTo(b.SecondsLeft));
        return bars;
    }

    // ---- persistence: the TimerDefinition record serializes directly ----

    private static string FilePath => Path.Combine(AppSettings.Directory, "timers.json");

    private void Load()
    {
        foreach (var definition in PersistedJsonFile.Load<List<TimerDefinition>>(FilePath, static () => []))
            Service.AddOrUpdateDefinition(definition);
    }

    private void Save()
    {
        // Snapshot under the lock, write OUTSIDE it — serialize+write used
        // to run while holding the GLOBAL sync, stalling every log pump.
        List<TimerDefinition> own;
        lock (_sync)
        {
            // The user's own definitions only — the Lexicon set is a synced
            // mirror, re-materialised from the pack cache on startup.
            own = Service.Definitions.Where(d => d.Source.Length == 0).ToList();
        }
        try
        {
            PersistedJsonFile.Save(FilePath, own);
        }
        catch (Exception)
        {
            // Persistence failure degrades to in-memory timers.
        }
    }
}
