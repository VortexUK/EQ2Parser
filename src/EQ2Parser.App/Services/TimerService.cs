using System.IO;
using System.Text.Json;
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
    bool Radial);

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
            Save();
        }
    }

    public void Remove(string key)
    {
        lock (_sync)
        {
            if (Service.RemoveDefinition(key))
                Save();
        }
    }

    public void SetEnabled(string key, bool enabled)
    {
        lock (_sync)
        {
            var current = Service.Definitions.FirstOrDefault(d => d.Key == key);
            if (current is null || current.Enabled == enabled)
                return;
            Service.AddOrUpdateDefinition(current with { Enabled = enabled });
            Save();
        }
    }

    public int ImportMany(IReadOnlyCollection<TimerDefinition> definitions)
    {
        if (definitions.Count == 0)
            return 0;
        lock (_sync)
        {
            foreach (var definition in definitions)
                Service.AddOrUpdateDefinition(definition);
            Save();
        }
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
    /// still fire in a test.</summary>
    public void StartTest(TimerDefinition definition)
    {
        lock (_sync)
        {
            Service.Notify("you", definition.Name, self: true, definition.Category, DateTimeOffset.Now);
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
                    bars.Add(new TimerBarSnapshot(
                        frame.Definition.Name,
                        frame.Combatant,
                        timer.SecondsLeft(now),
                        timer.DurationSeconds,
                        frame.Definition.WarningSeconds,
                        frame.Definition.FillColorArgb,
                        frame.Definition.RadialDisplay));
                }
            }
        }
        bars.Sort((a, b) => a.SecondsLeft.CompareTo(b.SecondsLeft));
        return bars;
    }

    // ---- persistence: the TimerDefinition record serializes directly ----

    private static string FilePath => Path.Combine(AppSettings.Directory, "timers.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;
            foreach (var definition in JsonSerializer.Deserialize<List<TimerDefinition>>(File.ReadAllText(FilePath)) ?? [])
                Service.AddOrUpdateDefinition(definition);
        }
        catch (Exception)
        {
            // Corrupt timer file never blocks startup.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Service.Definitions.ToList(), JsonOptions));
        }
        catch (Exception)
        {
            // Persistence failure degrades to in-memory timers.
        }
    }
}
