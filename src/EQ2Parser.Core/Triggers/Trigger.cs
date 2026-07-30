using System.Text.RegularExpressions;

namespace EQ2Parser.Core.Triggers;

/// <summary>Audio action attached to a trigger (ACT-compatible codes).</summary>
public enum TriggerSound
{
    None = 0,
    Beep = 1,
    WavFile = 2,
    Tts = 3,
}

/// <summary>
/// One custom trigger: a regex watched against every log line (timestamp
/// stripped), with the actions to take on match. Field set mirrors ACT's
/// CustomTrigger so the XML share format round-trips losslessly
/// (docs/act-behavior.md §4); our additions are per-trigger knobs for the
/// values ACT hardcodes.
/// </summary>
public sealed class Trigger
{
    public Trigger(string regexText, string? category = null)
    {
        RegexText = regexText;
        Category = string.IsNullOrWhiteSpace(category) ? "General" : category!;
        Pattern = new Regex(regexText, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        PrefilterLiteral = LiteralPrefilter.TryExtract(regexText);
    }

    public string RegexText { get; }
    public Regex Pattern { get; }

    /// <summary>Doubles as the zone-restriction scope; default "General".</summary>
    public string Category { get; }

    /// <summary>Identity for import/update-in-place, matching ACT's keying.</summary>
    public string Key => $"{Category}|{RegexText}";

    /// <summary>Cheap literal gate evaluated before the regex (null = none
    /// extractable, always run the regex). Never produces false negatives.</summary>
    public string? PrefilterLiteral { get; }

    public bool Enabled { get; init; } = true;

    /// <summary>When set, the trigger is only active while the current zone
    /// contains <see cref="Category"/> (case-insensitive substring, instance
    /// numbers stripped) — ACT semantics.</summary>
    public bool RestrictToCategoryZone { get; init; }

    public TriggerSound SoundType { get; init; } = TriggerSound.None;

    /// <summary>Wav path for <see cref="TriggerSound.WavFile"/>; TTS template
    /// (capture refs $1/${name} expand) for <see cref="TriggerSound.Tts"/>.</summary>
    public string SoundData { get; init; } = "";

    public bool StartsTimer { get; init; }
    public string TimerName { get; init; } = "";

    /// <summary>Minimum gap between audio alerts from this trigger.
    /// ACT hardcodes 1 s; ours is per-trigger configurable.</summary>
    public TimeSpan AudioCooldown { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Extracts a literal substring a regex REQUIRES so most lines can skip the
/// regex entirely. Conservative: returns null unless a literal run outside
/// any group/alternation/escape is found — a null just means "no shortcut".
/// </summary>
public static class LiteralPrefilter
{
    public static string? TryExtract(string pattern)
    {
        // Alternation at the top level makes any literal non-mandatory.
        var depth = 0;
        foreach (var c in pattern)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == '|' && depth == 0) return null;
        }

        var best = "";
        var current = "";
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c is '\\' or '(' or ')' or '[' or ']' or '{' or '}' or '^' or '$' or '.' or '|')
            {
                // A quantifier makes the PREVIOUS char optional/repeated —
                // handled below; escapes and structures end the literal run.
                Commit(ref best, ref current);
                if (c == '\\') i++; // skip the escaped char
                if (c == '[') { while (i < pattern.Length && pattern[i] != ']') i++; }
                if (c == '{') { while (i < pattern.Length && pattern[i] != '}') i++; }
                continue;
            }
            if (c is '*' or '+' or '?')
            {
                // The char before a quantifier isn't guaranteed: drop it.
                if (current.Length > 0)
                    current = current[..^1];
                Commit(ref best, ref current);
                continue;
            }
            current += c;
        }
        Commit(ref best, ref current);
        return best.Length >= 3 ? best : null;
    }

    private static void Commit(ref string best, ref string current)
    {
        if (current.Length > best.Length)
            best = current;
        current = "";
    }
}
