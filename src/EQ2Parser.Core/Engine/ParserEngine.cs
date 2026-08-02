using EQ2Parser.Core.Combat;

namespace EQ2Parser.Core.Engine;

/// <summary>Tunables with ACT-compatible defaults.</summary>
public sealed record EngineOptions
{
    /// <summary>Seconds of log silence after the last hostile action before
    /// the encounter ends (ACT default 6, user-configurable by decision).</summary>
    public double IdleEndSeconds { get; init; } = 6;
}

/// <summary>
/// Per-log-source combat engine (one instance per tailed log — the multi-log
/// correlator consumes the encounters of many engines). Reproduces ACT's
/// lifecycle contract (docs/act-behavior.md §2):
///   * the grammar calls <see cref="SetEncounter"/> before each action and
///     drops the action if it returns false,
///   * <see cref="AddSwing"/> throws when not in combat,
///   * the idle rule is evaluated on every line time BEFORE the line is
///     processed, ending a stale encounter first.
/// Single-threaded by design: feed it from one source in line order.
/// </summary>
public sealed class ParserEngine(string sourceId, string ownerName, EngineOptions? options = null)
{
    private readonly EngineOptions _options = options ?? new EngineOptions();
    private readonly List<Encounter> _history = [];
    private DateTimeOffset _lastHostileTime = DateTimeOffset.MinValue;
    private int _timeSorter;

    /// <summary>Identity of the log source feeding this engine (multi-log).</summary>
    public string SourceId { get; } = sourceId;

    /// <summary>The log owner's character name ("YOUR" resolution + ally seed).</summary>
    public string OwnerName { get; } = ownerName;

    public string CurrentZone { get; private set; } = "";
    public bool InCombat { get; private set; }
    public Encounter? ActiveEncounter { get; private set; }
    public IReadOnlyList<Encounter> History => _history;

    public event Action<Encounter>? EncounterEnded;

    /// <summary>Next per-line sequence number (same-second ordering).</summary>
    public int NextTimeSorter() => ++_timeSorter;

    /// <summary>Call once per log line, before processing it — evaluates the
    /// idle-end rule against the line's timestamp (log-time driven, exactly
    /// like ACT: a stale encounter only closes when the next line arrives).</summary>
    public void OnLineTime(DateTimeOffset lineTime)
    {
        if (InCombat && (lineTime - _lastHostileTime).TotalSeconds > _options.IdleEndSeconds)
            EndCombat();
    }

    public void ChangeZone(string zoneName)
    {
        // Zone changes do NOT end combat by themselves (ACT semantics);
        // the grammar decides whether a zone line should also EndCombat.
        CurrentZone = zoneName;
    }

    /// <summary>Start-or-continue contract: call before every parsed action.
    /// Returns false when the action should be dropped.
    ///
    /// Only HOSTILE actions (attack attempts — melee/non-melee swings and
    /// their avoids) start an encounter or extend its idle clock. Heals,
    /// wards, power, threat, cures and deaths are recorded while a fight is
    /// live but never prolong it — post-fight healing used to keep the
    /// encounter open long enough for the next pull to bleed in.</summary>
    public bool SetEncounter(DateTimeOffset time, string attacker, string victim, bool hostile = true)
    {
        if (!InCombat)
        {
            if (!hostile)
                return false;
            ActiveEncounter = new Encounter(SourceId, OwnerName, CurrentZone);
            _history.Add(ActiveEncounter);
            InCombat = true;
        }
        if (hostile)
            _lastHostileTime = time;
        return true;
    }

    /// <summary>Insert one combat action. Throws when not in combat — the
    /// grammar must have called <see cref="SetEncounter"/> first (ACT's
    /// exact contract, kept so ordering bugs surface loudly).</summary>
    public void AddSwing(
        SwingCategory category,
        bool critical,
        string special,
        string attacker,
        string ability,
        DamageValue damage,
        DateTimeOffset time,
        string victim,
        string damageType,
        string? extra = null,
        DateTimeOffset? observedAt = null)
    {
        if (!InCombat || ActiveEncounter is null)
            throw new InvalidOperationException("AddSwing requires an active encounter — call SetEncounter first.");

        // ACT-compatible normalisation.
        special = string.IsNullOrWhiteSpace(special) || special is "hit" or "hits" ? "None" : special;
        var swing = new Swing(
            category, critical, special,
            attacker.Trim(), ability.Trim(), damage,
            time, NextTimeSorter(), victim.Trim(), damageType, extra, observedAt);

        ActiveEncounter.AddSwing(swing);
    }

    public void EndCombat()
    {
        if (!InCombat || ActiveEncounter is null)
            return;
        InCombat = false;
        var encounter = ActiveEncounter;
        ActiveEncounter = null;
        encounter.End();
        // A fight that never resolved a real enemy title ("Encounter") is a
        // scrap — no identifiable opponent, usually owner-less. ACT discards
        // these too; drop it from history and never announce it.
        if (encounter.Title == Encounter.PlaceholderTitle)
        {
            _history.Remove(encounter);
            return;
        }
        EncounterEnded?.Invoke(encounter);
    }
}
