using System.Text.RegularExpressions;

namespace EQ2Parser.Core.Raid;

/// <summary>One tracked character in the current raid session.</summary>
public sealed class RaidMemberState
{
    public required string Name { get; init; }
    public bool InRaid { get; set; }
    public bool Online { get; set; }
    public bool Afk { get; set; }

    /// <summary>Guild membership evidence: true = seen in the guild who /
    /// Guildmate lines; false = in raid but PROVABLY absent from a completed
    /// guild who (can't receive guild points — excluded from DKP files);
    /// null = unknown (treated as guildie, best effort). True is sticky —
    /// wrongly excluding a real guildie from DKP outweighs one harmless
    /// failed points command for an outsider.</summary>
    public bool? InGuild { get; set; }

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
///     The refresh macro this app writes runs "whoraid" then
///     "who all guild". A whoraid block is self-identifying (its header
///     says so) and seeds the raid immediately. A PLAIN /who block is
///     ambiguous — "/who", "/who all" and "/who all guild" all echo the
///     same header shape — so it is trusted as the online-guildies seed
///     ONLY when it completes within <see cref="PairWindow"/> of a whoraid
///     block (the macro contract); any other plain block is ignored
///     entirely (a bare "/who all" lists arbitrary server players and
///     must never pollute the sit-out list).
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
                InGuild = m.InGuild,
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
        || WhoParser.LooksRelevant(message)
        || DkpAwardProgress.LooksRelevant(message);

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
        m.InGuild = true; // "Guildmate:" lines are guild-membership proof either way
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

    /// <summary>Classification of /who blocks (see class docs): whoraid
    /// blocks are self-identifying raid seeds; a plain block is the guild
    /// half only when it follows a whoraid block inside PairWindow. Any
    /// other plain block ("/who", "/who all", a manual lookup) is ignored
    /// — its rows are arbitrary players, not online guildies.</summary>
    private bool ApplyWhoBlock(WhoResult who)
    {
        if (who.FromWhoraid)
        {
            _pendingWho = who;
            return ApplyWhoRows(who, asRaid: true);
        }
        if (_pendingWho is { } raidHalf && who.CompletedAt - raidHalf.CompletedAt <= PairWindow)
        {
            _pendingWho = null;
            var changed = ApplyWhoRows(who, asRaid: false, asGuild: true);
            changed |= SweepNotInGuild(who);
            return changed;
        }
        return false;
    }

    private bool ApplyWhoRows(WhoResult who, bool asRaid, bool asGuild = false)
    {
        var changed = false;
        foreach (var row in who.Rows)
        {
            var m = Get(row.Name);
            m.Afk = row.Afk;
            m.Class ??= row.Class;
            m.Guild ??= row.Guild;
            if (asGuild)
                m.InGuild = true;
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

    /// <summary>A /who block this large may have hit the in-game display cap
    /// — absence from a truncated list proves nothing.</summary>
    public const int WhoDisplayCap = 100;

    /// <summary>The guild who lists EVERY online guildie, so an in-raid
    /// member absent from it is provably not in the guild (they can't
    /// receive guild points). Only flips unknown → false: confirmed-guildie
    /// evidence is sticky (see <see cref="RaidMemberState.InGuild"/>).</summary>
    private bool SweepNotInGuild(WhoResult guildWho)
    {
        if (guildWho.Rows.Count >= WhoDisplayCap)
            return false; // possibly truncated — draw no absence conclusions
        var guildNames = new HashSet<string>(guildWho.Rows.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var m in _members.Values)
        {
            if (m.InRaid && m.InGuild is null && !guildNames.Contains(m.Name))
            {
                m.InGuild = false;
                changed = true;
            }
        }
        return changed;
    }
}
