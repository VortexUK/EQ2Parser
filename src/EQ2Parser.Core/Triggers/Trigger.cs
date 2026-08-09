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
/// (docs/engine-behaviour.md §4); our additions are per-trigger knobs for the
/// values ACT hardcodes.
/// </summary>
public sealed class Trigger
{
    /// <summary>Per-match timeout on every trigger regex. Trigger patterns
    /// can be SERVER-SUPPLIED (the Lexicon pack) or pasted in (ACT-share
    /// import), and they run synchronously on the log-pump thread under the
    /// global lock — a catastrophic-backtracking pattern (e.g. "(a+)+$")
    /// would otherwise freeze the entire app. A legitimate trigger matches in
    /// microseconds; 100 ms is a vast safety margin that still aborts a ReDoS
    /// pattern fast. The engine disables a trigger whose match times out.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    public Trigger(string regexText, string? category = null, string? zone = null)
    {
        RegexText = regexText;
        Category = string.IsNullOrWhiteSpace(category) ? "General" : category!;
        Zone = string.IsNullOrWhiteSpace(zone) ? "" : zone!.Trim();
        Pattern = new Regex(regexText, RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);
        PrefilterLiteral = LiteralPrefilter.TryExtract(regexText);
    }

    private Trigger(Trigger source)
    {
        RegexText = source.RegexText;
        Category = source.Category;
        Zone = source.Zone;
        // Regex instances are immutable and thread-safe — share the compiled
        // pattern instead of recompiling on every enable flip.
        Pattern = source.Pattern;
        PrefilterLiteral = source.PrefilterLiteral;
        Enabled = source.Enabled;
        RestrictToCategoryZone = source.RestrictToCategoryZone;
        SoundType = source.SoundType;
        SoundData = source.SoundData;
        StartsTimer = source.StartsTimer;
        TimerName = source.TimerName;
        AudioCooldown = source.AudioCooldown;
        Source = source.Source;
    }

    /// <summary>Copy with a different enabled flag. Lives here — an
    /// app-side field-by-field clone silently drops any field added later.</summary>
    public Trigger WithEnabled(bool enabled) => new(this) { Enabled = enabled };

    public string RegexText { get; }
    public Regex Pattern { get; }

    /// <summary>The mob (or folder) the trigger files under — Zone → Category
    /// → triggers in the UI, like timers. For plain ACT imports this is also
    /// the zone-gate text (see <see cref="ZoneScope"/>).</summary>
    public string Category { get; }

    /// <summary>Display grouping AND the zone-gate text when set. Empty on
    /// plain ACT imports, whose Category traditionally holds the zone.</summary>
    public string Zone { get; }

    /// <summary>What the zone restriction actually tests against the current
    /// zone name: Zone when present, else Category (ACT back-compat — ACT
    /// has no Zone field, its restricted triggers put the zone in Category).</summary>
    public string ZoneScope => Zone.Length > 0 ? Zone : Category;

    /// <summary>Identity for import/update-in-place — zone-qualified like
    /// timers, so the same regex can exist per zone (a shared boss emote
    /// with different callouts, or zone-gated copies).</summary>
    public string Key => $"{Zone}|{Category}|{RegexText}";

    /// <summary>Cheap literal gate evaluated before the regex (null = none
    /// extractable, always run the regex). Never produces false negatives.</summary>
    public string? PrefilterLiteral { get; }

    public bool Enabled { get; init; } = true;

    /// <summary>When set, the trigger is only active while the current zone
    /// contains <see cref="ZoneScope"/> (case-insensitive substring, instance
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

    /// <summary>Where the trigger came from: "" = the user's own
    /// (persisted, editable), "lexicon" = synced from EQ2Lexicon
    /// (read-only, never persisted, replaced wholesale on re-sync).</summary>
    public string Source { get; init; } = "";
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
        // One escape-aware pass. Literals are only harvested at depth 0 —
        // anything inside a group is never guaranteed (the group may be
        // alternated or quantified away: "(stuns|dazes) you" must not yield
        // "stuns", "(Deathbringer )?DT" must not yield "Deathbringer ").
        var best = "";
        var current = "";
        var depth = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                // Escaped char: possibly a class (\d, \s) — end the run and
                // never let it affect depth/alternation tracking.
                Commit(ref best, ref current);
                i++;
                continue;
            }
            if (c == '(') { Commit(ref best, ref current); depth++; continue; }
            if (c == ')') { depth = Math.Max(0, depth - 1); continue; }
            if (c == '|')
            {
                // Top-level alternation: NOTHING in the pattern is mandatory.
                if (depth == 0)
                    return null;
                continue; // in-group: content isn't harvested anyway
            }
            if (depth > 0)
                continue;
            if (c == '[')
            {
                Commit(ref best, ref current);
                i++; // ']' as the class's first char is a literal — step past
                while (i < pattern.Length && pattern[i] != ']')
                    i += pattern[i] == '\\' ? 2 : 1;
                continue;
            }
            if (c == '{')
            {
                // {0,n} makes the previous char optional, exactly like '?'.
                if (i + 1 < pattern.Length && pattern[i + 1] == '0' && current.Length > 0)
                    current = current[..^1];
                Commit(ref best, ref current);
                while (i < pattern.Length && pattern[i] != '}') i++;
                continue;
            }
            if (c is '*' or '+' or '?')
            {
                // The char before a quantifier isn't guaranteed contiguous: drop it.
                if (current.Length > 0)
                    current = current[..^1];
                Commit(ref best, ref current);
                continue;
            }
            if (c is '^' or '$' or '.' or '}' or ']')
            {
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
