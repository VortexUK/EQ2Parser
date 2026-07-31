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

/// <summary>One live countdown instance.</summary>
public sealed class ActiveTimer(DateTimeOffset start, int durationSeconds, bool isMaster)
{
    public DateTimeOffset Start { get; } = start;
    public int DurationSeconds { get; } = durationSeconds;
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
        // attacker/victim/zone; a restricted match is preferred over an
        // unrestricted one (last of each kind wins — ACT order semantics).
        TimerDefinition? restricted = null, unrestricted = null;
        foreach (var def in candidates)
        {
            if (!def.Enabled)
                continue;
            if (def.RestrictToCategory)
            {
                var cat = def.Category.ToLowerInvariant();
                if (cat == attacker || cat == victim || cat == zone)
                    restricted = def;
            }
            else
            {
                unrestricted = def;
            }
        }
        var chosen = restricted ?? unrestricted;
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
        var timer = new ActiveTimer(time, chosen.DurationSeconds, isMaster);
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
