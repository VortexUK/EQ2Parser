namespace EQ2Parser.Core.Triggers;

/// <summary>
/// A spell-timer definition (ACT's TimerData) — field set mirrors ACT so the
/// &lt;Spell&gt; share format round-trips losslessly (docs/act-behavior.md §4).
/// </summary>
public sealed record TimerDefinition
{
    public required string Name { get; init; }
    public string Category { get; init; } = "General";

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
    public bool RadialDisplay { get; init; } = true;

    /// <summary>ACT's two spell-timer panels (the mini timer windows) — kept
    /// for lossless config import; our overlay maps A now, B later.</summary>
    public bool Panel1 { get; init; } = true;
    public bool Panel2 { get; init; }

    /// <summary>Bar colour as ARGB int (kept UI-framework-free in Core).</summary>
    public int FillColorArgb { get; init; } = unchecked((int)0xFF0000FF);

    public string Tooltip { get; init; } = "";
    public string StartSoundData { get; init; } = "";
    public string WarningSoundData { get; init; } = "";

    /// <summary>ACT identity: lower-cased category|name.</summary>
    public string Key => $"{Category.ToLowerInvariant()}|{Name.ToLowerInvariant()}";
}
