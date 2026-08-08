namespace EQ2Parser.Core.Combat;

/// <summary>
/// One fight, whatever witnessed it — a single log's <see cref="Encounter"/>,
/// a multi-log <see cref="Correlation.CorrelatedEncounter"/>, or an app-side
/// rollup. Consumers bind to this instead of switching on the concrete
/// shapes: the per-shape switch used to be re-implemented at every call
/// site, and forgotten arms were a recurring bug class.
/// </summary>
public interface IFightView
{
    string Zone { get; }
    string Title { get; }
    DateTimeOffset StartTime { get; }
    DateTimeOffset EndTime { get; }
    TimeSpan Duration { get; }

    /// <summary>Ally damage total (the fight's headline number).</summary>
    long Damage { get; }

    /// <summary>The upload/ranking-parity rate: damage over the REAL
    /// duration, 0 when the duration is 0 (act-behavior.md §3).</summary>
    double EncDps { get; }

    SuccessLevel GetSuccessLevel();

    /// <summary>Canonical whole-fight seconds for DISPLAY rate maths,
    /// clamped to ≥1. Log timestamps are whole seconds, so the only clamped
    /// case is a same-second fight — where dividing by the real duration
    /// renders a useless 0. Uploads deliberately keep the real duration
    /// (<see cref="EncDps"/>).</summary>
    double DisplaySeconds => Math.Max(1, Duration.TotalSeconds);

    /// <summary>The single encounter classification runs against — the
    /// encounter itself, or a merged fight's primary. Null for rollups,
    /// which classify per member fight.</summary>
    Encounter? ClassificationSource { get; }

    /// <summary>Every encounter classification runs against: the encounter
    /// itself, a merged fight's primary, or each member fight's primary for
    /// a rollup.</summary>
    IEnumerable<Encounter> ClassificationSources { get; }

    /// <summary>Authoritative per-combatant instances, keyed by combatant
    /// key. A rollup yields one entry per member fight, so the same key can
    /// repeat — single fights never repeat a key.</summary>
    IEnumerable<KeyValuePair<string, Combatant>> ViewCombatants { get; }

    /// <summary>The authoritative ally set, keyed by combatant key — a
    /// single log's resolved allies, or the merged ally view of a
    /// correlated fight. Rollups yield one entry per member fight.</summary>
    IEnumerable<KeyValuePair<string, Combatant>> AllyCombatants { get; }

    bool ContainsCombatant(string key);

    /// <summary>Every per-fight instance of one combatant (an aggregate
    /// yields one per member fight; single fights yield 0 or 1).</summary>
    IReadOnlyList<Combatant> InstancesOf(string key);
}
