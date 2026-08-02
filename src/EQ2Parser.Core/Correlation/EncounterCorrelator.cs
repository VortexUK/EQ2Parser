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

    /// <summary>Bumped on every history mutation — create, merge, delete,
    /// restore. Views cache against this instead of History.Count, which
    /// was blind to in-place merges (stale tree/zone-summary labels).</summary>
    public long Version { get; private set; }

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

    /// <summary>Register a log owner without attaching an engine — history
    /// restore uses this so the shared-combatant merge test skips owner
    /// names exactly like live correlation.</summary>
    public void RegisterOwner(string ownerName) =>
        _ownerKeys.Add(ownerName.ToUpperInvariant());

    /// <summary>Source removal: stop accepting this engine's encounters.
    /// The owner key stays registered — history from that owner still needs
    /// the owner rule for future merges.</summary>
    public void Detach(ParserEngine engine) => engine.EncounterEnded -= Accept;

    /// <summary>User deletion of a correlated fight. Returns false when the
    /// fight was not (or no longer) in history.</summary>
    public bool Remove(CorrelatedEncounter fight)
    {
        if (!_history.Remove(fight))
            return false;
        Version++;
        return true;
    }

    /// <summary>Undo of a user deletion: re-insert the fight at its
    /// chronological spot (history stays ordered by start time).</summary>
    public void Restore(CorrelatedEncounter fight)
    {
        if (_history.Contains(fight))
            return;
        var index = _history.Count;
        while (index > 0 && _history[index - 1].StartTime > fight.StartTime)
            index--;
        _history.Insert(index, fight);
        Version++;
    }

    /// <summary>Direct entry for tests/imports.</summary>
    public void Accept(Encounter encounter)
    {
        foreach (var candidate in Enumerable.Reverse(_history))
        {
            // Cheap window pre-check — a CONTINUE, not a break: undo-restores
            // and mid-session archive pull-backs insert out of end-time
            // order, so an early-ending candidate can still be followed (in
            // the walk) by one that overlaps. IsSameFight re-checks the
            // window; this just skips its zone/combatant work.
            if (candidate.EndTime < encounter.StartTime - _options.TimeTolerance)
                continue;
            if (IsSameFight(candidate, encounter))
            {
                candidate.Join(encounter);
                Version++;
                Merged?.Invoke(candidate);
                return;
            }
        }
        var created = new CorrelatedEncounter(encounter);
        _history.Add(created);
        Version++;
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
