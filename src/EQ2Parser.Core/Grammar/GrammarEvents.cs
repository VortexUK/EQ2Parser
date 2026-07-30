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
    string DamageType) : GrammarEvent;

/// <summary>A kill/death line → a Death swing under the Killing pseudo-ability.</summary>
public sealed record DeathEvent(string Killer, string Victim) : GrammarEvent;

/// <summary>"You have entered X." — zone change (does not end combat by itself).</summary>
public sealed record ZoneEvent(string ZoneName) : GrammarEvent;
