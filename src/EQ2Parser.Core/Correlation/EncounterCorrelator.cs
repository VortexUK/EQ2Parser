using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;

namespace EQ2Parser.Core.Correlation;

public sealed record CorrelatorOptions
{
    /// <summary>How far apart two sources' windows may sit and still be the
    /// same fight (covers idle-timeout skew between logs).</summary>
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Groups completed encounters from many per-source engines into canonical
/// fights. Two encounters correlate when ALL of:
///   * same zone (verbatim; canonical zone mapping is a site concern),
///   * time windows overlap within <see cref="CorrelatorOptions.TimeTolerance"/>,
///   * they share at least one combatant that is not one of the log owners
///     (the boss/adds — two unrelated fights in the same zone at the same
///     time share owners' groupmates only when they ARE the same fight).
/// Completion-based by design, mirroring the site's mirror-grouping.
/// </summary>
public sealed class EncounterCorrelator(CorrelatorOptions? options = null)
{
    private readonly CorrelatorOptions _options = options ?? new CorrelatorOptions();
    private readonly List<CorrelatedEncounter> _history = [];
    private readonly HashSet<string> _ownerKeys = new(StringComparer.Ordinal);

    public IReadOnlyList<CorrelatedEncounter> History => _history;

    /// <summary>Raised when an encounter lands in a NEW correlated fight.</summary>
    public event Action<CorrelatedEncounter>? Created;

    /// <summary>Raised when a source joins an existing correlated fight.</summary>
    public event Action<CorrelatedEncounter>? Merged;

    /// <summary>Subscribe to an engine's completed encounters. One correlator,
    /// many engines — one per tailed log.</summary>
    public void Attach(ParserEngine engine)
    {
        _ownerKeys.Add(engine.OwnerName.ToUpperInvariant());
        engine.EncounterEnded += Accept;
    }

    /// <summary>User deletion of a correlated fight. Returns false when the
    /// fight was not (or no longer) in history.</summary>
    public bool Remove(CorrelatedEncounter fight) => _history.Remove(fight);

    /// <summary>Direct entry for tests/imports.</summary>
    public void Accept(Encounter encounter)
    {
        foreach (var candidate in Enumerable.Reverse(_history))
        {
            // History is chronological; once candidates end far before this
            // encounter starts, nothing older can match.
            if (candidate.EndTime < encounter.StartTime - _options.TimeTolerance)
                break;
            if (IsSameFight(candidate, encounter))
            {
                candidate.Join(encounter);
                Merged?.Invoke(candidate);
                return;
            }
        }
        var created = new CorrelatedEncounter(encounter);
        _history.Add(created);
        Created?.Invoke(created);
    }

    private bool IsSameFight(CorrelatedEncounter candidate, Encounter encounter)
    {
        if (!string.Equals(candidate.Zone, encounter.Zone, StringComparison.OrdinalIgnoreCase))
            return false;

        // A source never merges with itself (two pulls seen by one log are
        // two fights, however close together).
        if (candidate.Sources.Any(s => s.SourceId == encounter.SourceId))
            return false;

        var overlapStart = candidate.StartTime - _options.TimeTolerance;
        var overlapEnd = candidate.EndTime + _options.TimeTolerance;
        if (encounter.EndTime < overlapStart || encounter.StartTime > overlapEnd)
            return false;

        // Shared non-owner combatant — the mob(s) both logs witnessed.
        foreach (var source in candidate.Sources)
        {
            foreach (var key in encounter.Combatants.Keys)
            {
                if (_ownerKeys.Contains(key))
                    continue;
                if (source.Combatants.ContainsKey(key))
                    return true;
            }
        }
        return false;
    }
}
