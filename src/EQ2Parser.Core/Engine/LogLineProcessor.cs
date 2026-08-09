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
    /// <summary>Max lag between a line's log time and its arrival before it
    /// counts as replayed history rather than live play (clock skew between
    /// the game's stamp and ours stays well under this).</summary>
    public static readonly TimeSpan TriggerFreshness = TimeSpan.FromSeconds(30);

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
                    _timers.NotifyLinked(t.TimerName, t.Zone, t.Category, t.Attacker, t.Victim, LastLineTime, Engine.CurrentZone);
            };
        }
    }

    public ParserEngine Engine { get; }

    /// <summary>Timestamp of the most recent processed line.</summary>
    public DateTimeOffset LastLineTime { get; private set; }

    /// <summary>A LIVE status-effect apply (victim, log effect word,
    /// arrival) — feeds the mass-detriment callout monitor. Replayed
    /// history never raises it (same freshness rule as triggers).</summary>
    public event Action<string, string, DateTimeOffset>? StatusApplied;

    /// <summary>Raised (log-pump thread) when a LIVE chat line carries an
    /// ACT trigger-share snippet from ANOTHER player — the app offers to
    /// add it. Never fires during catch-up/replay or for own pastes.</summary>
    public event Action<Triggers.SharedTrigger>? TriggerShared;

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
        // Triggers and timers are live alerts: a line only fires them when
        // it was written moments before we read it. Replaying an old file
        // (parse-from-start) must never spam beeps/TTS/timer bars for
        // history. No arrival stamp (direct feeds, tests) means live.
        var live = line.ObservedAt is null || line.ObservedAt.Value - line.Timestamp < TriggerFreshness;
        if (live)
            _triggers?.Process(line.Message, anchor);

        // Trigger shares pasted into chat (the ACT community convention).
        // Live-only for the same reason as alerts: replaying an old log
        // must not re-offer every share ever seen. Own pastes are skipped —
        // the sharer obviously has the trigger.
        if (live && TriggerShared is not null && line.Message.Contains("<Trigger", StringComparison.Ordinal)
            && Triggers.ChatTriggerShare.TryExtract(line.Message) is { Self: false } share)
        {
            TriggerShared.Invoke(share);
        }

        // Scripted-win say lines (bosses that end by script, not death) —
        // only consulted while a fight is live; StartsWith early-out keeps
        // the per-line cost at a single character compare.
        if (Engine.ActiveEncounter is { } scriptedActive && ScriptedWins.Default.TryMatch(line.Message))
            scriptedActive.ScriptedWin = true;

        var parsed = EnglishGrammar.TryParse(line.Message);
        if (parsed is null)
            return;
        LinesMatched++;

        switch (parsed)
        {
            case ZoneEvent zone:
                Engine.ChangeZone(zone.ZoneName, line.Timestamp);
                _triggers?.SetZone(zone.ZoneName);
                break;

            case InstanceLockoutEvent lockout:
                Engine.ApplyInstanceLockout(lockout.Remaining, line.Timestamp);
                break;

            case SwingEvent swing:
                {
                    var attacker = Resolve(swing.Attacker);
                    var victim = Resolve(swing.Victim);
                    // Mass-detriment callouts must see status lines even when no
                    // fight is running (pre-pull stuns, no-damage script phases) —
                    // fire BEFORE the encounter gate below can break.
                    if (live && swing.Category == SwingCategory.StatusEffect && swing.DamageType == "applied")
                        StatusApplied?.Invoke(victim, swing.Ability, anchor);
                    // Self-damage procs are not hostile action — they must not
                    // start a fight or keep one alive.
                    var hostile = swing.Category is SwingCategory.Melee or SwingCategory.NonMelee
                        && !string.Equals(attacker, victim, StringComparison.OrdinalIgnoreCase);
                    if (!Engine.SetEncounter(line.Timestamp, attacker, victim, hostile))
                        break;
                    // The avoid-actor rides in Extra ("by=YOU") and needs the
                    // same owner resolution as attacker/victim.
                    var extra = swing.Extra;
                    if (extra is not null && extra.StartsWith("by=", StringComparison.Ordinal))
                        extra = "by=" + Resolve(extra[3..]);
                    Engine.AddSwing(
                        swing.Category, swing.Critical, swing.Special,
                        attacker, swing.Ability, swing.Damage,
                        line.Timestamp, victim, swing.DamageType, extra, line.ObservedAt);
                    // Every combat action notifies the spell timers by ability
                    // name (ACT semantics) — how cast-driven timers start. Timers
                    // anchor to the arrival stamp so bars start the instant the
                    // line lands, not on the next whole second. Replayed history
                    // never starts bars (same freshness rule as triggers).
                    if (live && _timers is not null)
                    {
                        _timers.Notify(
                            attacker, swing.Ability,
                            self: attacker == Engine.OwnerName || victim == Engine.OwnerName,
                            victim, anchor, Engine.CurrentZone);
                        // Recast debuffs (Traumatic Swipe): every hit refreshes
                        // the victim's timer mod, ACT-style; a cure stripping a
                        // known debuff drops the mod again.
                        _timers.NotifyRecastDebuff(attacker, victim, swing.Ability, anchor);
                        if (swing.Category == SwingCategory.CureDispel)
                            _timers.NotifyDispel(victim, swing.DamageType, anchor);
                    }
                    break;
                }

            case DeathEvent death:
                {
                    var killer = Resolve(death.Killer);
                    var victim = Resolve(death.Victim);
                    // In the owner's OWN log a real self-death is always second
                    // person ("Avatar of Growth has killed you.") — the game
                    // never writes the log owner's death in third person. So a
                    // third-person death naming the OWNER can only be an entity
                    // acting under their name: the Templar hammer temp pet et
                    // al. Verified empirically (2026-08, 685MB of raid logs):
                    // 82 own-name deaths were all hammer expiries on the summon
                    // cadence; the 16 real deaths were all "killed you", with
                    // zero overlap between the shapes. ACT counts them all as
                    // the player — a deliberate, documented deviation.
                    var petDeath = death.Victim.Equals(Engine.OwnerName, StringComparison.OrdinalIgnoreCase);
                    // Death drops the timer mods ON the combatant AND the ones
                    // they applied — a dead swiper's debuff dies with them, and
                    // running timers it stretched rescale pro-rata. A pet death
                    // must NOT strip the owner's timers.
                    if (live && !petDeath)
                        _timers?.NotifyDeath(victim, anchor);
                    // Deaths are recorded into a live fight but never start one —
                    // an out-of-combat "Alas, X has died" is not an encounter.
                    if (!Engine.SetEncounter(line.Timestamp, killer, victim, hostile: false))
                        break;
                    Engine.AddSwing(
                        SwingCategory.Melee, false, "None",
                        killer, Combatant.KillingAbility, DamageValue.Death,
                        line.Timestamp, victim, "death",
                        extra: petDeath ? Combatant.PetDeathExtra : null,
                        observedAt: line.ObservedAt);
                    break;
                }
        }
    }

    // "YOURSELF" appears as the victim of self-targeted lines ("…absorbs 720
    // points of damage from being done to YOURSELF."), and kill lines use
    // lowercase second person ("Avatar of Growth has killed you.") — the
    // case-sensitive match sent the owner's REAL deaths to a stray
    // combatant literally named "you". All you-forms are the log owner.
    private string Resolve(string name) =>
        name.Equals("you", StringComparison.OrdinalIgnoreCase)
        || name.Equals("yourself", StringComparison.OrdinalIgnoreCase)
            ? Engine.OwnerName
            : name;
}
