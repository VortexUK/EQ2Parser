using EQ2Parser.Core.Combat;

namespace EQ2Parser.Core.Grammar;

/// <summary>Base of everything the grammar can extract from one log line.</summary>
public abstract record GrammarEvent;

/// <summary>A combat action → one engine swing.</summary>
public sealed record SwingEvent(
    SwingCategory Category,
    bool Critical,
    string Special,
    string Attacker,
    string Ability,
    DamageValue Damage,
    string Victim,
    string DamageType,
    string? Extra = null) : GrammarEvent;

/// <summary>A kill/death line → a Death swing under the Killing pseudo-ability.</summary>
public sealed record DeathEvent(string Killer, string Victim) : GrammarEvent;

/// <summary>"You have entered X." — zone change (does not end combat by itself).</summary>
public sealed record ZoneEvent(string ZoneName) : GrammarEvent;

/// <summary>"This instance will expire in 7 days." — printed on the line
/// after (or the same second as) the zone-in when entering a persisted
/// instance. Remaining is the lockout left; entry time + Remaining is the
/// instance's expiry, which identifies the instance: a reset produces a
/// fresh full-lockout expiry, a re-entry reproduces the old one.</summary>
public sealed record InstanceLockoutEvent(TimeSpan Remaining) : GrammarEvent;
