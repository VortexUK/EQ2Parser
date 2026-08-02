using EQ2Parser.Core.Combat;

namespace EQ2Parser.Core.Correlation;

/// <summary>One combatant's authoritative view within a merged encounter.</summary>
/// <param name="Combatant">The chosen source's data for this combatant.</param>
/// <param name="AuthoritySourceId">Which log source won authority.</param>
/// <param name="IsSourceOwner">True when authority came from the combatant's
/// own log (always wins — EQ2 logs fully record only their owner).</param>
public sealed record MergedCombatant(Combatant Combatant, string AuthoritySourceId, bool IsSourceOwner);

/// <summary>
/// One fight as witnessed by one or more log sources (the multi-log headline
/// feature — docs/act-behavior.md "Improvements over ACT" §1). Grouping and
/// per-combatant authority mirror the EQ2Lexicon mirror-grouping design,
/// upgraded from per-fight-primary to per-combatant and computed client-side.
/// </summary>
public sealed class CorrelatedEncounter
{
    private readonly List<Encounter> _sources = [];
    private Dictionary<string, MergedCombatant>? _mergedCache;

    internal CorrelatedEncounter(Encounter first) => _sources.Add(first);

    public IReadOnlyList<Encounter> Sources => _sources;

    /// <summary>The primary source: longest duration (the site's
    /// primary-upload rule). Title/success/zone read from it.</summary>
    public Encounter Primary => _sources.MaxBy(e => e.Duration)!;

    public string Zone => Primary.Zone;
    public string Title => Primary.Title;
    public SuccessLevel GetSuccessLevel() => Primary.GetSuccessLevel();

    public DateTimeOffset StartTime => _sources.Min(e => e.StartTime);
    public DateTimeOffset EndTime => _sources.Max(e => e.EndTime);
    public TimeSpan Duration => EndTime > StartTime ? EndTime - StartTime : TimeSpan.Zero;

    internal void Join(Encounter encounter)
    {
        _sources.Add(encounter);
        _mergedCache = null;
    }

    /// <summary>
    /// Per-combatant authoritative merge: a combatant's own log always wins
    /// for that combatant; otherwise the source that observed them most
    /// completely (highest swing count).
    /// </summary>
    public IReadOnlyDictionary<string, MergedCombatant> MergedCombatants
    {
        get
        {
            if (_mergedCache is not null)
                return _mergedCache;
            var merged = new Dictionary<string, MergedCombatant>(StringComparer.Ordinal);
            // Owner name → the source ids logging as that character. A plain
            // ToDictionary threw when two sources share an owner name (the
            // same character name on two servers, or a re-added rotation) —
            // each such source keeps own-log authority for its combatant.
            var ownerKeys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var s in _sources)
            {
                var owner = s.OwnerName.ToUpperInvariant();
                if (!ownerKeys.TryGetValue(owner, out var ids))
                    ownerKeys[owner] = ids = [];
                ids.Add(s.SourceId);
            }

            foreach (var source in _sources)
            {
                foreach (var (key, combatant) in source.Combatants)
                {
                    var isOwnLog = ownerKeys.TryGetValue(key, out var owningSources) && owningSources.Contains(source.SourceId);
                    if (!merged.TryGetValue(key, out var current))
                    {
                        merged[key] = new MergedCombatant(combatant, source.SourceId, isOwnLog);
                        continue;
                    }
                    if (current.IsSourceOwner)
                        continue; // own-log authority is never displaced
                    if (isOwnLog || combatant.ObservedSwingCount > current.Combatant.ObservedSwingCount)
                        merged[key] = new MergedCombatant(combatant, source.SourceId, isOwnLog);
                }
            }
            return _mergedCache = merged;
        }
    }

    /// <summary>Merged ally keys: the union of every source's resolved allies.</summary>
    public IReadOnlySet<string> MergedAllyKeys
    {
        get
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in _sources)
                foreach (var ally in source.GetAllies())
                    keys.Add(ally.Key);
            return keys;
        }
    }

    /// <summary>Merged raid damage: authoritative per-combatant totals summed
    /// over the merged ally set — more complete than any single log's view.</summary>
    public long Damage
    {
        get
        {
            var allies = MergedAllyKeys;
            long sum = 0;
            foreach (var (key, mc) in MergedCombatants)
                if (allies.Contains(key))
                    sum += mc.Combatant.Damage;
            return sum;
        }
    }

    public double EncDps
    {
        get
        {
            var seconds = Duration.TotalSeconds;
            return seconds > 0 ? Damage / seconds : 0;
        }
    }
}
