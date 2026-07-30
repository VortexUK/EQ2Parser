using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Grammar;
using EQ2Parser.Core.Logs;

namespace EQ2Parser.Core.Engine;

/// <summary>
/// The per-source driver: raw <see cref="LogLine"/>s in, engine state out.
/// Resolves the grammar's "YOU" marker to the log owner, applies the ACT
/// lifecycle contract (idle check per line, SetEncounter before every swing),
/// and turns death events into Killing-ability swings.
/// </summary>
public sealed class LogLineProcessor(ParserEngine engine)
{
    public ParserEngine Engine { get; } = engine;

    /// <summary>Lines seen / lines that produced a grammar event — the golden
    /// harness's coverage counters.</summary>
    public long LinesSeen { get; private set; }
    public long LinesMatched { get; private set; }

    public void Process(in LogLine line)
    {
        LinesSeen++;
        Engine.OnLineTime(line.Timestamp);

        var parsed = EnglishGrammar.TryParse(line.Message);
        if (parsed is null)
            return;
        LinesMatched++;

        switch (parsed)
        {
            case ZoneEvent zone:
                Engine.ChangeZone(zone.ZoneName);
                break;

            case SwingEvent swing:
            {
                var attacker = Resolve(swing.Attacker);
                var victim = Resolve(swing.Victim);
                if (!Engine.SetEncounter(line.Timestamp, attacker, victim))
                    break;
                Engine.AddSwing(
                    swing.Category, swing.Critical, swing.Special,
                    attacker, swing.Ability, swing.Damage,
                    line.Timestamp, victim, swing.DamageType);
                break;
            }

            case DeathEvent death:
            {
                var killer = Resolve(death.Killer);
                var victim = Resolve(death.Victim);
                if (!Engine.SetEncounter(line.Timestamp, killer, victim))
                    break;
                Engine.AddSwing(
                    SwingCategory.Melee, false, "None",
                    killer, Combatant.KillingAbility, DamageValue.Death,
                    line.Timestamp, victim, "death");
                break;
            }
        }
    }

    private string Resolve(string name) =>
        name is EnglishGrammar.You or "You" ? Engine.OwnerName : name;
}
