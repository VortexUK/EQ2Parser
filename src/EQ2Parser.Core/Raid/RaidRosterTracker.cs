using System.Text.RegularExpressions;

namespace EQ2Parser.Core.Raid;

/// <summary>One tracked character in the current raid session.</summary>
public sealed class RaidMemberState
{
    public required string Name { get; init; }
    public bool InRaid { get; set; }
    public bool Online { get; set; }
    public bool Afk { get; set; }
    public string? Class { get; set; }
    public string? Guild { get; set; }
    public DateTimeOffset? RaidFirstSeen { get; set; }
    public DateTimeOffset? RaidLastSeen { get; set; }
    public DateTimeOffset? OnlineFirstSeen { get; set; }
    public DateTimeOffset? OnlineLastSeen { get; set; }
}

/// <summary>
/// The raid-attendance brain: accumulates "who is in the raid" and "which
/// guildmates are online" for the current session from four log signals
/// (shapes mined from real logs, 2026-09):
///
///  1. /who blocks (via <see cref="WhoParser"/>) — the only true roster dump.
///     The refresh macro this app writes runs "/who all raid" then
///     "/who all guild", so two blocks completing within
///     <see cref="PairWindow"/> are classified positionally: first = raid
///     seed, second = online-guildies seed. A lone block only contributes
///     online evidence (safe default — raid membership then builds from
///     deltas and fight allies).
///  2. Raid deltas: "X has joined the raid." / "X has left the raid." /
///     "X's group has joined/left the raid." (leader-change suffix variant).
///     Blind to members present before our own join — hence 1 and 4.
///  3. Guild presence: "Guildmate: X has logged in." / "logged out."
///     (trailing period; the "Friend:" variant is ignored — not guild-scoped).
///  4. Fight allies: union each finished fight's player-shaped allies into
///     the raid (catches pre-join members the deltas miss).
///
/// Thread-safe (lock + injectable time via the per-line timestamps);
/// multi-source safe — state is keyed by name, so N log sources observing
/// the same raid converge. Callers must only feed LIVE lines (replayed
/// history must never mutate the current session).
/// </summary>
public sealed partial class RaidRosterTracker
{
    public static readonly TimeSpan PairWindow = TimeSpan.FromSeconds(10);

    [GeneratedRegex(@"^(?<name>[A-Za-z]+) has (?<dir>joined|left) the raid\.$")]
    private static partial Regex MemberDeltaRegex();

    [GeneratedRegex(@"^(?<name>[A-Za-z]+)'s group has (?<dir>joined|left) the raid[.,]")]
    private static partial Regex GroupDeltaRegex();

    [GeneratedRegex(@"^Guildmate: (?<name>[A-Za-z]+) has logged (?<dir>in|out)\.$")]
    private static partial Regex GuildPresenceRegex();

    private readonly Dictionary<string, RaidMemberState> _members = new(StringComparer.OrdinalIgnoreCase);
    private readonly WhoParser _who = new();
    private WhoResult? _pendingWho;
    private DateTimeOffset _sessionStarted;
    private readonly object _gate = new();

    /// <summary>Raised (on the pump thread) whenever the roster changes —
    /// UI refresh via its own tick; don't do heavy work here.</summary>
    public event Action? RosterChanged;

    public DateTimeOffset SessionStarted
    {
        get { lock (_gate) return _sessionStarted; }
    }

    /// <summary>Snapshot of every tracked member (copy — safe off-thread).</summary>
    public IReadOnlyList<RaidMemberState> Snapshot()
    {
        lock (_gate)
        {
            return [.. _members.Values.Select(m => new RaidMemberState
            {
                Name = m.Name,
                InRaid = m.InRaid,
                Online = m.Online,
                Afk = m.Afk,
                Class = m.Class,
                Guild = m.Guild,
                RaidFirstSeen = m.RaidFirstSeen,
                RaidLastSeen = m.RaidLastSeen,
                OnlineFirstSeen = m.OnlineFirstSeen,
                OnlineLastSeen = m.OnlineLastSeen,
            })];
        }
    }

    /// <summary>Wipe everything and start a fresh session (new raid night).</summary>
    public void StartNewSession(DateTimeOffset now)
    {
        lock (_gate)
        {
            _members.Clear();
            _pendingWho = null;
            _sessionStarted = now;
        }
        RosterChanged?.Invoke();
    }

    /// <summary>Cheap prefilter for the pump-thread hook — only lines that
    /// pass this are worth handing to <see cref="OnLine"/>.</summary>
    public static bool LooksRelevant(string message) =>
        message.Contains(" the raid", StringComparison.Ordinal)
        || message.StartsWith("Guildmate: ", StringComparison.Ordinal)
        || WhoParser.LooksRelevant(message);

    /// <summary>Feed one LIVE log line (any source).</summary>
    public void OnLine(string message, DateTimeOffset time)
    {
        var changed = false;
        lock (_gate)
        {
            if (_who.Feed(message, time) is { } who)
            {
                changed = ApplyWhoBlock(who);
            }
            else if (MemberDeltaRegex().Match(message) is { Success: true } m)
            {
                changed = ApplyRaidDelta(m.Groups["name"].Value, m.Groups["dir"].Value == "joined", time);
            }
            else if (GroupDeltaRegex().Match(message) is { Success: true } g)
            {
                // Group deltas name only the leader; their groupmates surface
                // via individual deltas / who / fight allies.
                changed = ApplyRaidDelta(g.Groups["name"].Value, g.Groups["dir"].Value == "joined", time);
            }
            else if (GuildPresenceRegex().Match(message) is { Success: true } p)
            {
                changed = ApplyPresence(p.Groups["name"].Value, p.Groups["dir"].Value == "in", time);
            }
        }
        if (changed)
            RosterChanged?.Invoke();
    }

    /// <summary>Union a finished fight's player allies into the raid —
    /// catches members present before our own raid join.</summary>
    public void OnFightAllies(IEnumerable<string> playerNames, DateTimeOffset fightEnd)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var name in playerNames)
            {
                if (!Combat.Swing.LooksLikePlayer(name))
                    continue;
                changed |= MarkInRaid(Get(name), fightEnd);
            }
        }
        if (changed)
            RosterChanged?.Invoke();
    }

    // ── internals (all under _gate) ─────────────────────────────────────────

    private RaidMemberState Get(string name)
    {
        if (!_members.TryGetValue(name, out var m))
            _members[name] = m = new RaidMemberState { Name = name };
        return m;
    }

    private bool ApplyRaidDelta(string name, bool joined, DateTimeOffset time)
    {
        var m = Get(name);
        if (joined)
            return MarkInRaid(m, time);
        m.InRaid = false;
        m.RaidLastSeen = time;
        return true;
    }

    private static bool MarkInRaid(RaidMemberState m, DateTimeOffset time)
    {
        var changed = !m.InRaid;
        m.InRaid = true;
        m.Online = true;
        m.RaidFirstSeen ??= time;
        m.RaidLastSeen = time;
        m.OnlineFirstSeen ??= time;
        m.OnlineLastSeen = time;
        return changed;
    }

    private bool ApplyPresence(string name, bool loggedIn, DateTimeOffset time)
    {
        var m = Get(name);
        m.Online = loggedIn;
        if (loggedIn)
        {
            m.OnlineFirstSeen ??= time;
            m.OnlineLastSeen = time;
        }
        else
        {
            m.OnlineLastSeen = time;
            // Logging out also removes you from the raid roster view.
            if (m.InRaid)
            {
                m.InRaid = false;
                m.RaidLastSeen = time;
            }
        }
        return true;
    }

    /// <summary>Positional classification of /who blocks (see class docs):
    /// the FIRST block of a close pair seeds the raid, the SECOND seeds the
    /// online-guildies set. A block with no partner inside PairWindow is
    /// online-evidence only.</summary>
    private bool ApplyWhoBlock(WhoResult who)
    {
        bool changed;
        if (_pendingWho is { } first && who.CompletedAt - first.CompletedAt <= PairWindow)
        {
            changed = ApplyWhoRows(first, asRaid: true);
            changed |= ApplyWhoRows(who, asRaid: false);
            _pendingWho = null;
        }
        else
        {
            // Hold this block; if a partner lands inside the window the pair
            // rule fires above. Apply online evidence immediately either way
            // (it is correct under both interpretations).
            changed = ApplyWhoRows(who, asRaid: false);
            _pendingWho = who;
        }
        return changed;
    }

    private bool ApplyWhoRows(WhoResult who, bool asRaid)
    {
        var changed = false;
        foreach (var row in who.Rows)
        {
            var m = Get(row.Name);
            m.Afk = row.Afk;
            m.Class ??= row.Class;
            m.Guild ??= row.Guild;
            if (asRaid)
            {
                changed |= MarkInRaid(m, who.CompletedAt);
            }
            else
            {
                m.Online = true;
                m.OnlineFirstSeen ??= who.CompletedAt;
                m.OnlineLastSeen = who.CompletedAt;
                changed = true;
            }
        }
        return changed;
    }
}
