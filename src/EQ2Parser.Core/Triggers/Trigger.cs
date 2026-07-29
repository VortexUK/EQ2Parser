using System.Text.RegularExpressions;

namespace EQ2Parser.Core.Triggers;

/// <summary>
/// One user-defined trigger: a regex watched against every parsed log line,
/// with the actions to take when it matches. Mirrors the capability set of
/// ACT's Custom Triggers (sound / TTS / countdown timer) — the ACT trigger
/// XML share format imports into this shape.
/// </summary>
/// <param name="Id">Stable identity (used for de-dup on import and settings persistence).</param>
/// <param name="Pattern">Compiled regex evaluated against <see cref="Logs.LogLine.Message"/>.</param>
/// <param name="SoundFile">Optional sound to play on match.</param>
/// <param name="Tts">Optional text-to-speech line; may reference capture groups as $1/${name}.</param>
/// <param name="TimerName">Optional countdown-timer name to start on match.</param>
/// <param name="TimerDuration">Duration for <paramref name="TimerName"/>.</param>
public sealed record Trigger(
    string Id,
    Regex Pattern,
    string? SoundFile = null,
    string? Tts = null,
    string? TimerName = null,
    TimeSpan? TimerDuration = null);
