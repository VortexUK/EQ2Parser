namespace EQ2Parser.Core.Triggers;

/// <summary>
/// Watches live status-effect applies and calls out mass detriments —
/// "8 players stunned" — without any per-boss configuration.
///
/// Mechanics: applies funnel per CANONICAL control effect (the log's
/// "mesmerized"/"dazzled" both count as mez) into a sliding window of
/// distinct player victims. When the window reaches <see cref="MinVictims"/>,
/// the callout is held for <see cref="CollectDelay"/> so the rest of the
/// AoE's lines land and the announced count is the wave's real size, then
/// fires once and cools down — a 24-line stun wave is one callout, not 22.
///
/// Victims are player-shaped names only (single capitalised word): mobs the
/// RAID is chain-mezzing are articled ("a bloom custodian") and never
/// counted; single-word named mobs slip the filter but a threshold ≥ 2
/// screens them out since one mob is one victim.
///
/// Time is injected (apply + tick) so tests are deterministic.
/// </summary>
public sealed class StatusCalloutMonitor
{
    /// <summary>Distinct player victims inside the window before a callout.</summary>
    public int MinVictims { get; set; } = 3;

    /// <summary>How long an apply counts toward the wave.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Per-effect quiet period after a callout.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(12);

    /// <summary>Threshold-to-callout hold so the whole wave is counted.</summary>
    public static readonly TimeSpan CollectDelay = TimeSpan.FromMilliseconds(900);

    /// <summary>(canonical effect, distinct player count).</summary>
    public event Action<string, int>? Callout;

    private sealed class EffectState
    {
        public readonly Dictionary<string, DateTimeOffset> Recent = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset? PendingSince;
        public DateTimeOffset LastCallout = DateTimeOffset.MinValue;
    }

    private readonly Dictionary<string, EffectState> _effects = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void OnStatusApplied(string victim, string logWord, DateTimeOffset now)
    {
        if (Vocabulary.CanonicalControlEffect(logWord) is not { } effect || !LooksLikePlayer(victim))
            return;
        lock (_gate)
        {
            if (!_effects.TryGetValue(effect, out var state))
                _effects[effect] = state = new EffectState();
            state.Recent[victim] = now;
            Prune(state, now);
            if (now - state.LastCallout < Cooldown)
                return;
            if (state.PendingSince is null && state.Recent.Count >= MinVictims)
                state.PendingSince = now;
        }
    }

    /// <summary>Flush pending callouts whose collection hold elapsed.
    /// Drive from the app tick (~100 ms).</summary>
    public void Tick(DateTimeOffset now)
    {
        List<(string Effect, int Count)>? fire = null;
        lock (_gate)
        {
            foreach (var (effect, state) in _effects)
            {
                if (state.PendingSince is not { } since || now - since < CollectDelay)
                    continue;
                state.PendingSince = null;
                state.LastCallout = now;
                Prune(state, now);
                if (state.Recent.Count >= MinVictims)
                    (fire ??= []).Add((effect, state.Recent.Count));
            }
        }
        if (fire is null)
            return;
        foreach (var (effect, count) in fire)
            Callout?.Invoke(effect, count);
    }

    private void Prune(EffectState state, DateTimeOffset now)
    {
        List<string>? stale = null;
        foreach (var (victim, at) in state.Recent)
        {
            if (now - at > Window)
                (stale ??= []).Add(victim);
        }
        foreach (var victim in stale ?? [])
            state.Recent.Remove(victim);
    }

    /// <summary>Player names are one capitalised word; trash mobs carry
    /// articles ("a bisected rumbler") and nameds are usually multi-word.</summary>
    public static bool LooksLikePlayer(string name) =>
        name.Length > 1 && char.IsUpper(name[0]) && !name.Contains(' ');
}
