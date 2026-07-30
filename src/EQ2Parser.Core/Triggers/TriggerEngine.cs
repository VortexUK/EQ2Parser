using System.Text.RegularExpressions;

namespace EQ2Parser.Core.Triggers;

/// <summary>A timer the matched trigger wants started.</summary>
public sealed record TimerRequest(string TimerName, string Attacker, string Victim);

/// <summary>Everything a matched trigger wants done. The engine decides;
/// the app layer performs (audio, TTS, timer bars).</summary>
public sealed record TriggerFired(
    Trigger Trigger,
    Match Match,
    bool PlayBeep,
    string? WavFile,
    string? TtsText,
    TimerRequest? Timer);

/// <summary>
/// Evaluates the active trigger set against each log line's message
/// (timestamp already stripped by the LogLine parse). ACT-compatible
/// semantics (docs/act-behavior.md §4):
///   * a capture group literally named YOU must contain the owner's name or
///     the match is dropped,
///   * zone restriction is a case-insensitive substring test of Category
///     against the current zone with a trailing instance number stripped,
///   * audio is rate-limited per trigger (configurable, ACT hardcoded 1 s),
///   * timer requests read optional "attacker"/"victim" groups (default
///     "None").
/// Improvements over ACT: the active set rebuilds on ANY edit (not just zone
/// change), and a per-trigger literal prefilter skips the regex for most
/// lines. Synchronous by design — matching is microseconds, and firing in
/// the line path is what makes alerts feel instant.
/// </summary>
public sealed class TriggerEngine(string ownerName)
{
    private readonly Dictionary<string, Trigger> _all = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastAudio = new(StringComparer.Ordinal);
    private Trigger[] _active = [];
    private string _zone = "";

    public string OwnerName { get; } = ownerName;

    public event Action<TriggerFired>? Fired;

    public IReadOnlyCollection<Trigger> Triggers => _all.Values;
    public IReadOnlyList<Trigger> ActiveTriggers => _active;

    /// <summary>Add or update (by ACT identity key) — active set rebuilds
    /// immediately, no zone change required.</summary>
    public void AddOrUpdate(Trigger trigger)
    {
        _all[trigger.Key] = trigger;
        RebuildActive();
    }

    public bool Remove(string key)
    {
        var removed = _all.Remove(key);
        if (removed)
            RebuildActive();
        return removed;
    }

    public void SetZone(string zoneName)
    {
        _zone = zoneName;
        RebuildActive();
    }

    private void RebuildActive()
    {
        // Strip a trailing instance number: "Kaeldun Keep 2" → "Kaeldun Keep".
        var zone = _zone;
        var lastSpace = zone.LastIndexOf(' ');
        if (lastSpace > 0 && int.TryParse(zone.AsSpan(lastSpace + 1), out _))
            zone = zone[..lastSpace];

        _active = _all.Values
            .Where(t => t.Enabled &&
                        (!t.RestrictToCategoryZone
                         || zone.Contains(t.Category, StringComparison.OrdinalIgnoreCase)
                         || _zone.Contains(t.Category, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    /// <summary>Evaluate one line. <paramref name="time"/> is the line's log
    /// time — it drives the audio rate limit deterministically.</summary>
    public void Process(string message, DateTimeOffset time)
    {
        foreach (var trigger in _active)
        {
            if (trigger.PrefilterLiteral is not null &&
                !message.Contains(trigger.PrefilterLiteral, StringComparison.Ordinal))
                continue;

            var match = trigger.Pattern.Match(message);
            if (!match.Success)
                continue;

            // The YOU-group convention: only fire for the log owner.
            var youGroup = match.Groups["YOU"];
            if (youGroup.Success && !youGroup.Value.Contains(OwnerName, StringComparison.OrdinalIgnoreCase))
                continue;

            var audioAllowed = !_lastAudio.TryGetValue(trigger.Key, out var last)
                               || time - last >= trigger.AudioCooldown;
            var wantsAudio = trigger.SoundType != TriggerSound.None;
            if (wantsAudio && audioAllowed)
                _lastAudio[trigger.Key] = time;

            TimerRequest? timer = null;
            if (trigger.StartsTimer && trigger.TimerName.Length > 0)
            {
                var attacker = match.Groups["attacker"] is { Success: true } a ? a.Value : "None";
                var victim = match.Groups["victim"] is { Success: true } v ? v.Value : "None";
                timer = new TimerRequest(trigger.TimerName, attacker, victim);
            }

            Fired?.Invoke(new TriggerFired(
                trigger,
                match,
                PlayBeep: wantsAudio && audioAllowed && trigger.SoundType == TriggerSound.Beep,
                WavFile: wantsAudio && audioAllowed && trigger.SoundType == TriggerSound.WavFile ? trigger.SoundData : null,
                TtsText: wantsAudio && audioAllowed && trigger.SoundType == TriggerSound.Tts ? match.Result(trigger.SoundData) : null,
                Timer: timer));
        }
    }
}
