using System.Text.RegularExpressions;
using EQ2Parser.Core.Logs;

namespace EQ2Parser.Core.Triggers;

/// <summary>A trigger that matched a log line, with its regex captures.</summary>
public sealed record TriggerMatch(Trigger Trigger, LogLine Line, Match Match)
{
    /// <summary>
    /// The trigger's TTS template with $1/${name} capture references expanded
    /// from the match — what the app should actually speak.
    /// </summary>
    public string? ExpandedTts => Trigger.Tts is null ? null : Match.Result(Trigger.Tts);
}

/// <summary>
/// Evaluates the active trigger set against each parsed log line. Pure and
/// synchronous — the UI layer owns audio/TTS/timer side effects by
/// subscribing to <see cref="Fired"/>. Kept allocation-light because it runs
/// for every line of a live raid log.
/// </summary>
public sealed class TriggerEngine
{
    private Trigger[] _triggers = [];

    /// <summary>Raised once per (matching trigger, line) pair, in trigger order.</summary>
    public event Action<TriggerMatch>? Fired;

    /// <summary>Replace the active trigger set atomically (UI thread edits, parse thread reads).</summary>
    public void SetTriggers(IEnumerable<Trigger> triggers) =>
        Volatile.Write(ref _triggers, triggers.ToArray());

    public IReadOnlyList<Trigger> Triggers => Volatile.Read(ref _triggers);

    public void Process(in LogLine line)
    {
        var triggers = Volatile.Read(ref _triggers);
        foreach (var trigger in triggers)
        {
            var match = trigger.Pattern.Match(line.Message);
            if (match.Success)
                Fired?.Invoke(new TriggerMatch(trigger, line, match));
        }
    }
}
