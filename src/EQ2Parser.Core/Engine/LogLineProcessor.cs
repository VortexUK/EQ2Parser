using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Grammar;
using EQ2Parser.Core.Logs;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Core.Engine;

/// <summary>
/// The per-source driver: raw <see cref="LogLine"/>s in, engine state out.
/// Resolves the grammar's "YOU" marker to the log owner, applies the ACT
/// lifecycle contract (idle check per line, SetEncounter before every swing),
/// turns death events into Killing-ability swings, and — when composed with
/// a <see cref="TriggerEngine"/>/<see cref="SpellTimerService"/> — evaluates
/// triggers per line and notifies spell timers for every combat action
/// (ACT's NotifySpell semantics), all synchronously in the line path so
/// alerts fire within the tail-poll latency (~10 ms).
/// </summary>
public sealed class LogLineProcessor
{
    private readonly TriggerEngine? _triggers;
    private readonly SpellTimerService? _timers;

    public LogLineProcessor(ParserEngine engine, TriggerEngine? triggers = null, SpellTimerService? timers = null)
    {
        Engine = engine;
        _triggers = triggers;
        _timers = timers;
        if (_triggers is not null && _timers is not null)
        {
            // A trigger's timer request behaves like ACT: Self forced true.
            _triggers.Fired += fired =>
            {
                if (fired.Timer is { } t)
                    _timers.Notify(t.Attacker, t.TimerName, self: true, t.Victim, LastLineTime, Engine.CurrentZone);
            };
        }
    }

    public ParserEngine Engine { get; }

    /// <summary>Timestamp of the most recent processed line.</summary>
    public DateTimeOffset LastLineTime { get; private set; }

    /// <summary>Lines seen / lines that produced a grammar event — the golden
    /// harness's coverage counters.</summary>
    public long LinesSeen { get; private set; }
    public long LinesMatched { get; private set; }

    public void Process(in LogLine line)
    {
        LinesSeen++;
        // The alert/timer anchor: arrival stamp when live, log time on import.
        // Stat math below stays on line.Timestamp (the compatibility clock).
        var anchor = line.ObservedAt ?? line.Timestamp;
        LastLineTime = anchor;
        Engine.OnLineTime(line.Timestamp);
        _triggers?.Process(line.Message, anchor);

        var parsed = EnglishGrammar.TryParse(line.Message);
        if (parsed is null)
            return;
        LinesMatched++;

        switch (parsed)
        {
            case ZoneEvent zone:
                Engine.ChangeZone(zone.ZoneName);
                _triggers?.SetZone(zone.ZoneName);
                break;

            case SwingEvent swing:
            {
                var attacker = Resolve(swing.Attacker);
                var victim = Resolve(swing.Victim);
                var hostile = swing.Category is SwingCategory.Melee or SwingCategory.NonMelee;
                if (!Engine.SetEncounter(line.Timestamp, attacker, victim, hostile))
                    break;
                Engine.AddSwing(
                    swing.Category, swing.Critical, swing.Special,
                    attacker, swing.Ability, swing.Damage,
                    line.Timestamp, victim, swing.DamageType, swing.Extra, line.ObservedAt);
                // Every combat action notifies the spell timers by ability
                // name (ACT semantics) — how cast-driven timers start. Timers
                // anchor to the arrival stamp so bars start the instant the
                // line lands, not on the next whole second.
                _timers?.Notify(
                    attacker, swing.Ability,
                    self: attacker == Engine.OwnerName || victim == Engine.OwnerName,
                    victim, anchor, Engine.CurrentZone);
                break;
            }

            case DeathEvent death:
            {
                var killer = Resolve(death.Killer);
                var victim = Resolve(death.Victim);
                // Deaths are recorded into a live fight but never start one —
                // an out-of-combat "Alas, X has died" is not an encounter.
                if (!Engine.SetEncounter(line.Timestamp, killer, victim, hostile: false))
                    break;
                Engine.AddSwing(
                    SwingCategory.Melee, false, "None",
                    killer, Combatant.KillingAbility, DamageValue.Death,
                    line.Timestamp, victim, "death", extra: null, observedAt: line.ObservedAt);
                break;
            }
        }
    }

    // "YOURSELF" appears as the victim of self-targeted lines ("…absorbs 720
    // points of damage from being done to YOURSELF.") — same person as YOU.
    private string Resolve(string name) =>
        name is EnglishGrammar.You or "You" or "YOURSELF" or "Yourself" ? Engine.OwnerName : name;
}
