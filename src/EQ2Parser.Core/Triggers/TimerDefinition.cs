namespace EQ2Parser.Core.Triggers;

/// <summary>
/// A spell-timer definition (ACT's TimerData) — field set mirrors ACT so the
/// &lt;Spell&gt; share format round-trips losslessly (docs/act-behavior.md §4).
/// </summary>
public sealed record TimerDefinition
{
    public required string Name { get; init; }

    /// <summary>The mob (or zone) the category restriction matches. May
    /// hold several |-separated alternatives for mobs that split into
    /// renamed copies mid-fight ("the earth rumbler|bisected
    /// rumbler|trisected rumbler") — any of the names matches, and all
    /// share ONE timer. Our extension; ACT's Category is a single name.</summary>
    public string Category { get; init; } = "General";

    /// <summary>ACT's restrict-to-category check over every |-separated
    /// alternative in <see cref="Category"/>. Inputs pre-lowercased.</summary>
    public bool CategoryMatches(string attacker, string victim, string zone)
    {
        foreach (var raw in Category.Split('|'))
        {
            var name = raw.Trim().ToLowerInvariant();
            if (name.Length > 0 && (name == attacker || name == victim || name == zone))
                return true;
        }
        return false;
    }

    /// <summary>Display grouping only (Zone → Mob → timers in the UI) —
    /// no engine semantics and not part of the ACT share format.</summary>
    public string Zone { get; init; } = "";

    /// <summary>What the ability deals, as logged ("heat", "poison,
    /// disease"). Display metadata for bar colouring/labels — no engine
    /// semantics, not in the ACT share format.</summary>
    public string DamageType { get; init; } = "";

    /// <summary>The control effect it applies, in tooltip vocabulary
    /// ("stifle", "stun", "mesmerized"). Display metadata — the timer
    /// windows surface it so a bar says WHAT is about to land.</summary>
    public string ControlEffect { get; init; } = "";

    /// <summary>ACT's Checked flag: a disabled definition never starts a
    /// bar but stays in the list.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Base duration, seconds. ACT default 30.</summary>
    public int DurationSeconds { get; init; } = 30;

    /// <summary>Seconds remaining at which the warning fires. ACT default 10.</summary>
    public int WarningSeconds { get; init; } = 10;

    /// <summary>Seconds PAST expiry before the bar is removed (negative =
    /// linger). ACT default −15.</summary>
    public int RemoveSeconds { get; init; } = -15;

    /// <summary>"One only": refuse a new master while one is running.</summary>
    public bool AbsoluteTiming { get; init; }

    public bool RestrictToMe { get; init; }

    /// <summary>Require Category to equal the attacker, victim or current
    /// zone (lower-cased) for the timer to start.</summary>
    public bool RestrictToCategory { get; init; }

    /// <summary>Never create sub-timers; every notify is a fresh master.</summary>
    public bool OnlyMasterTicks { get; init; }

    /// <summary>"Allow Timer Mods to affect this": whether recast mods
    /// (ApplyTimerMod — final = base × (1 + mods)) scale this timer's
    /// duration at start. Off = always the base duration.</summary>
    public bool Modable { get; init; } = true;

    /// <summary>Round dial instead of a bar. Default OFF (ACT's default) —
    /// the dial row is for the two or three glance-constantly timers, and
    /// bars carry the flame styling.</summary>
    public bool RadialDisplay { get; init; }

    /// <summary>ACT's two spell-timer panels (the mini timer windows) — kept
    /// for lossless config import; our overlay maps A now, B later.</summary>
    public bool Panel1 { get; init; } = true;
    public bool Panel2 { get; init; }

    /// <summary>Bar colour as ARGB int (kept UI-framework-free in Core).</summary>
    public int FillColorArgb { get; init; } = unchecked((int)0xFF0000FF);

    public string Tooltip { get; init; } = "";
    public string StartSoundData { get; init; } = "";
    public string WarningSoundData { get; init; } = "";

    /// <summary>ACT's category|name identity, extended with zone: the same
    /// mob+ability in two zones are DISTINCT timers (bosses recur across
    /// zones with retuned abilities); the runtime prefers the current
    /// zone's version when several could start.</summary>
    public string Key => $"{Zone.ToLowerInvariant()}|{Category.ToLowerInvariant()}|{Name.ToLowerInvariant()}";
}
