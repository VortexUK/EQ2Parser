namespace EQ2Parser.Core.Triggers;

/// <summary>Runtime knobs with ACT's hardcoded values as defaults
/// (configurable per user decision).</summary>
public sealed record TimerOptions
{
    /// <summary>A re-notify within this window of the frame's newest timer is
    /// ignored outright (ACT: 2 s).</summary>
    public TimeSpan RetriggerIgnore { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A re-notify within this window creates a sub-timer instead of
    /// a fresh master (ACT: 12 s).</summary>
    public TimeSpan SubTimerWindow { get; init; } = TimeSpan.FromSeconds(12);
}

/// <summary>One mod contribution baked into a running timer: which debuff,
/// who applied it (lower-cased), and its additive fraction — kept so the
/// applier's death can rescale the timer pro-rata.</summary>
public sealed record AppliedTimerMod(string Name, string Attacker, double Fraction);

/// <summary>One live countdown instance. Duration may exceed the
/// definition's base when timer mods applied at start (final = base ×
/// (1 + mods)); the base, mod owner, and per-mod contributors are kept so
/// the grace-window reverts and applier-death rescales can adjust it.</summary>
public sealed class ActiveTimer(DateTimeOffset start, int durationSeconds, bool isMaster)
{
    public DateTimeOffset Start { get; } = start;
    public int DurationSeconds { get; internal set; } = durationSeconds;
    public int BaseDurationSeconds { get; internal init; } = durationSeconds;
    /// <summary>Lower-cased combatant whose mods scaled this timer ("" = unmodified).</summary>
    public string ModOwner { get; internal init; } = "";
    /// <summary>The mods that scaled this timer at start (empty = unmodified).</summary>
    public List<AppliedTimerMod> AppliedMods { get; internal init; } = [];
    public bool IsMaster { get; } = isMaster;
    public bool WarningRaised { get; internal set; }
    public bool ExpiryRaised { get; internal set; }

    public double SecondsLeft(DateTimeOffset now) =>
        DurationSeconds - (now - Start).TotalSeconds;
}

/// <summary>All live timers for one (spell, combatant) pair — one bar group.</summary>
public sealed class TimerFrame(TimerDefinition definition, string combatant)
{
    public TimerDefinition Definition { get; } = definition;
    public string Combatant { get; } = combatant;
    public List<ActiveTimer> Timers { get; } = [];
    public string Key => $"{Definition.Name} - {Combatant}";

    public DateTimeOffset NewestStart =>
        Timers.Count == 0 ? DateTimeOffset.MinValue : Timers.Max(t => t.Start);

    public bool HasRunningMaster(DateTimeOffset now) =>
        Timers.Any(t => t.IsMaster && t.SecondsLeft(now) > 0);
}

/// <summary>
/// The spell-timer runtime (ACT's FormSpellTimers.NotifySpell semantics,
/// docs/act-behavior.md §4). UI-free: consumers subscribe to the events and
/// call <see cref="Tick"/> on their display cadence with interpolated log
/// time — timers tick on LOG time, not wall time.
/// </summary>
public sealed class SpellTimerService(TimerOptions? options = null)
{
    private readonly TimerOptions _options = options ?? new TimerOptions();
    private readonly Dictionary<string, TimerDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TimerDefinition>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimerFrame> _frames = new(StringComparer.Ordinal);
    // owner (lower) → mod name → (additive fraction, expiry, applier).
    // ACT's recast mods: same name never stacks (re-apply refreshes and the
    // newest applier owns it), different names sum, and each mod lives only
    // for its debuff duration. The applier is tracked so their death can
    // drop the debuff and rescale running timers.
    private readonly Dictionary<string, Dictionary<string, (double Fraction, DateTimeOffset Expires, string Attacker)>> _mods = new(StringComparer.Ordinal);

    /// <summary>ACT's hardcoded recast-debuff table (ACT_English_Parser:
    /// every Traumatic Swipe hit applies a 50% recast slow to the victim
    /// for 30 s, refreshed per hit).</summary>
    public static readonly IReadOnlyDictionary<string, (double Fraction, TimeSpan Duration)> RecastDebuffs =
        new Dictionary<string, (double, TimeSpan)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Traumatic Swipe"] = (0.5, TimeSpan.FromSeconds(30)),
        };

    public event Action<TimerFrame, ActiveTimer>? TimerStarted;
    public event Action<TimerFrame, ActiveTimer>? WarningReached;
    public event Action<TimerFrame, ActiveTimer>? TimerExpired;
    public event Action<TimerFrame>? FrameRemoved;

    public IReadOnlyCollection<TimerFrame> Frames => _frames.Values;
    public IReadOnlyCollection<TimerDefinition> Definitions => _definitions.Values;

    public void AddOrUpdateDefinition(TimerDefinition definition)
    {
        _definitions[definition.Key] = definition;
        RebuildNameIndex();
    }

    public bool RemoveDefinition(string key)
    {
        var removed = _definitions.Remove(key);
        if (removed)
            RebuildNameIndex();
        return removed;
    }

    private void RebuildNameIndex()
    {
        _byName.Clear();
        foreach (var def in _definitions.Values)
        {
            if (!_byName.TryGetValue(def.Name, out var list))
                _byName[def.Name] = list = [];
            list.Add(def);
        }
    }

    /// <summary>
    /// The single entry point: every combat action notifies with its ability
    /// name; trigger timer-requests notify with Self forced true (ACT
    /// semantics). Returns true when a timer started or extended.
    /// </summary>
    public bool Notify(string attacker, string spellName, bool self, string victim, DateTimeOffset time, string currentZone = "")
    {
        if (!_byName.TryGetValue(spellName, out var candidates))
            return false;

        attacker = attacker.ToLowerInvariant();
        victim = victim.ToLowerInvariant();
        var zone = currentZone.ToLowerInvariant();

        // Candidate selection: category-restricted definitions must match
        // attacker/victim/zone and beat unrestricted ones (ACT semantics);
        // within each kind, a definition whose Zone matches the current
        // zone beats zone-less/other-zone twins — the same boss recurs
        // across zones with retuned abilities. Last wins within a tier.
        TimerDefinition? chosen = null;
        var chosenTier = -1;
        foreach (var def in candidates)
        {
            if (!def.Enabled)
                continue;
            var zoneHit = def.Zone.Length > 0 && def.Zone.ToLowerInvariant() == zone;
            int tier;
            if (def.RestrictToCategory)
            {
                if (!def.CategoryMatches(attacker, victim, zone))
                    continue;
                tier = zoneHit ? 3 : 2;
            }
            else
            {
                tier = zoneHit ? 1 : 0;
            }
            if (tier >= chosenTier)
            {
                chosen = def;
                chosenTier = tier;
            }
        }
        if (chosen is null)
            return false;
        if (chosen.RestrictToMe && !self)
            return false;

        var frame = GetOrCreateFrame(chosen, victim);

        // One-only: refuse while a master runs.
        if (chosen.AbsoluteTiming && frame.HasRunningMaster(time))
            return false;

        var sinceNewest = time - frame.NewestStart;
        if (frame.Timers.Count > 0 && sinceNewest < _options.RetriggerIgnore)
            return false; // dedupe

        var isMaster = chosen.OnlyMasterTicks || frame.Timers.Count == 0 || sinceNewest >= _options.SubTimerWindow;
        // Timer mods (ACT ApplyTimerMod): a Modable timer starting now is
        // scaled by the CASTER's active mods — final = base × (1 + Σ mods).
        // Non-modable definitions ignore mods entirely.
        var duration = chosen.DurationSeconds;
        var modOwner = "";
        List<AppliedTimerMod> applied = [];
        if (chosen.Modable)
        {
            applied = ActiveMods(attacker, time);
            if (applied.Count > 0)
            {
                duration = Math.Max(0, (int)Math.Round(duration * (1 + applied.Sum(m => m.Fraction))));
                modOwner = attacker;
            }
        }
        var timer = new ActiveTimer(time, duration, isMaster)
        {
            BaseDurationSeconds = chosen.DurationSeconds,
            ModOwner = modOwner,
            AppliedMods = applied,
        };
        if (isMaster)
        {
            // A fresh master resets the sound latches (sub-timers do not).
            foreach (var t in frame.Timers)
            {
                t.WarningRaised = true;
                t.ExpiryRaised = true;
            }
        }
        frame.Timers.Add(timer);
        TimerStarted?.Invoke(frame, timer);
        return true;
    }

    private TimerFrame GetOrCreateFrame(TimerDefinition def, string combatant)
    {
        var key = $"{def.Name} - {combatant}";
        if (!_frames.TryGetValue(key, out var frame))
            _frames[key] = frame = new TimerFrame(def, combatant);
        return frame;
    }

    // ---- timer mods (ACT ApplyTimerMod, docs/act-behavior.md §4) ----

    /// <summary>Add or refresh a named recast mod on a combatant for
    /// <paramref name="duration"/>. Same name never stacks (re-apply
    /// refreshes the expiry and the newest applier owns it); different
    /// names sum additively. Affects only timers that START while the mod
    /// is active.</summary>
    public void AddTimerMod(string owner, string modName, double fraction, DateTimeOffset time, TimeSpan duration, string attacker = "")
    {
        owner = owner.ToLowerInvariant();
        if (!_mods.TryGetValue(owner, out var ownerMods))
            _mods[owner] = ownerMods = new Dictionary<string, (double, DateTimeOffset, string)>(StringComparer.OrdinalIgnoreCase);
        ownerMods[modName] = (fraction, time + duration, attacker.ToLowerInvariant());
    }

    /// <summary>A damaging ability landed: if it's a known recast debuff
    /// (<see cref="RecastDebuffs"/>), apply/refresh the victim's mod —
    /// exactly ACT's per-hit ApplyTimerMod call, remembering who swiped.</summary>
    public bool NotifyRecastDebuff(string attacker, string victim, string ability, DateTimeOffset time)
    {
        if (!RecastDebuffs.TryGetValue(ability, out var debuff))
            return false;
        AddTimerMod(victim, ability, debuff.Fraction, time, debuff.Duration, attacker);
        return true;
    }

    /// <summary>A cure stripped an effect: if that effect was a recast mod
    /// on the victim, it drops (ACT's DispellTimerMods).</summary>
    public void NotifyDispel(string victim, string effect, DateTimeOffset time)
    {
        if (RecastDebuffs.ContainsKey(effect))
            RemoveTimerMod(victim, effect, time);
    }

    private List<AppliedTimerMod> ActiveMods(string owner, DateTimeOffset time)
    {
        if (!_mods.TryGetValue(owner, out var ownerMods))
            return [];
        List<string>? expired = null;
        List<AppliedTimerMod> active = [];
        foreach (var (name, mod) in ownerMods)
        {
            if (mod.Expires <= time)
                (expired ??= []).Add(name);
            else
                active.Add(new AppliedTimerMod(name, mod.Attacker, mod.Fraction));
        }
        if (expired is not null)
        {
            foreach (var name in expired)
                ownerMods.Remove(name);
            if (ownerMods.Count == 0)
                _mods.Remove(owner);
        }
        return active;
    }

    /// <summary>Every death routes here: mods ON the dead combatant drop
    /// (with the 2 s grace revert), and mods APPLIED BY them drop too — the
    /// debuff dies with its owner, so running timers it stretched rescale
    /// pro-rata: elapsed time stays spent, the remaining portion shrinks
    /// back to the unmodified rate. (Beyond ACT, which never touches a
    /// committed recast.)</summary>
    public void NotifyDeath(string name, DateTimeOffset time)
    {
        ClearTimerMods(name, time);
        var dead = name.ToLowerInvariant();

        // Drop pending mods this combatant applied to anyone.
        List<string>? emptyOwners = null;
        foreach (var (owner, ownerMods) in _mods)
        {
            List<string>? removed = null;
            foreach (var (modName, mod) in ownerMods)
            {
                if (mod.Attacker == dead)
                    (removed ??= []).Add(modName);
            }
            if (removed is null)
                continue;
            foreach (var modName in removed)
                ownerMods.Remove(modName);
            if (ownerMods.Count == 0)
                (emptyOwners ??= []).Add(owner);
        }
        if (emptyOwners is not null)
        {
            foreach (var owner in emptyOwners)
                _mods.Remove(owner);
        }

        // Rescale running timers that carry the dead applier's mods.
        foreach (var frame in _frames.Values)
        {
            foreach (var timer in frame.Timers)
            {
                if (timer.AppliedMods.Count == 0 || !timer.AppliedMods.Any(m => m.Attacker == dead))
                    continue;
                var elapsed = (time - timer.Start).TotalSeconds;
                var remaining = timer.DurationSeconds - elapsed;
                if (elapsed < 0 || remaining <= 0)
                    continue;
                var oldSum = timer.AppliedMods.Sum(m => m.Fraction);
                timer.AppliedMods.RemoveAll(m => m.Attacker == dead);
                var newSum = timer.AppliedMods.Sum(m => m.Fraction);
                timer.DurationSeconds = Math.Max(
                    timer.BaseDurationSeconds,
                    (int)Math.Round(elapsed + remaining * (1 + newSum) / (1 + oldSum)));
            }
        }
    }

    /// <summary>Remove one mod (debuff expired or dispelled). A dispel
    /// arriving within 1 s of a modified timer's start reverts that timer to
    /// its base duration — the recast never really committed.</summary>
    public void RemoveTimerMod(string owner, string modName, DateTimeOffset time)
    {
        owner = owner.ToLowerInvariant();
        if (_mods.TryGetValue(owner, out var ownerMods) && ownerMods.Remove(modName) && ownerMods.Count == 0)
            _mods.Remove(owner);
        RevertRecentlyModified(owner, time, TimeSpan.FromSeconds(1));
    }

    /// <summary>The combatant died: all their mods drop, and their modified
    /// timers started within the last 2 s revert to base duration.</summary>
    public void ClearTimerMods(string owner, DateTimeOffset time)
    {
        owner = owner.ToLowerInvariant();
        if (_mods.Remove(owner))
            RevertRecentlyModified(owner, time, TimeSpan.FromSeconds(2));
    }

    private void RevertRecentlyModified(string owner, DateTimeOffset time, TimeSpan window)
    {
        foreach (var frame in _frames.Values)
        {
            foreach (var timer in frame.Timers)
            {
                if (timer.ModOwner == owner
                    && timer.DurationSeconds != timer.BaseDurationSeconds
                    && time - timer.Start <= window)
                {
                    timer.DurationSeconds = timer.BaseDurationSeconds;
                    timer.AppliedMods.Clear();
                }
            }
        }
    }

    /// <summary>Advance to <paramref name="now"/>: raise warnings/expiries,
    /// drop timers past their remove window, drop empty frames.</summary>
    public void Tick(DateTimeOffset now)
    {
        List<string>? deadFrames = null;
        foreach (var frame in _frames.Values)
        {
            var def = frame.Definition;
            frame.Timers.RemoveAll(t =>
            {
                var left = t.SecondsLeft(now);
                if (!t.WarningRaised && left <= def.WarningSeconds)
                {
                    t.WarningRaised = true;
                    WarningReached?.Invoke(frame, t);
                }
                if (!t.ExpiryRaised && left <= 0)
                {
                    t.ExpiryRaised = true;
                    TimerExpired?.Invoke(frame, t);
                }
                // RemoveSeconds is negative when bars linger past zero.
                return left <= def.RemoveSeconds;
            });
            if (frame.Timers.Count == 0)
                (deadFrames ??= []).Add(frame.Key);
        }
        if (deadFrames is not null)
        {
            foreach (var key in deadFrames)
            {
                var frame = _frames[key];
                _frames.Remove(key);
                FrameRemoved?.Invoke(frame);
            }
        }
    }
}
