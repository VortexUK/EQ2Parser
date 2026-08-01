using EQ2Parser.Core.Combat;
using EQ2Parser.Core.History;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.Services;

/// <summary>One enemy ability as observed across archived fights: how often
/// it was cast, its measured recast rhythm — raw and swipe-adjusted — and
/// what it did. BaseIntervalSeconds normalizes each interval by its recast-
/// debuff coverage (time under Traumatic Swipe counts at 1/1.5 rate), so it
/// estimates the UNmodified recast: the right duration to store, because
/// the live engine re-stretches Modable timers when a swipe lands.</summary>
public sealed record MinedAbility(
    string Zone,
    string Mob,
    string Ability,
    int Casts,
    int Fights,
    double? MedianIntervalSeconds,
    double? MinIntervalSeconds,
    double? BaseIntervalSeconds,
    double SwipeCoverage,
    long TotalDamage,
    double AvgTargets,
    double TicksPerCast,
    bool IsMelee);

/// <summary>A mob and its mined abilities.</summary>
public sealed record MinedMob(string Zone, string Mob, IReadOnlyList<MinedAbility> Abilities);

/// <summary>
/// Mines the parse archive for what enemies actually cast: per zone → mob →
/// ability, clustering same-instant multi-target hits into single casts
/// (an AoE hitting 20 raiders is ONE cast) and measuring the intervals
/// between casts within each fight — the median is the timer duration as
/// reality reports it, immune to decade-old folklore.
/// </summary>
public static class AbilityMiner
{
    /// <summary>Hits within this window of the previous hit are one VOLLEY
    /// (multi-target landing + a tick of lag).</summary>
    private static readonly TimeSpan CastCluster = TimeSpan.FromSeconds(2);

    /// <summary>Volleys within this window of the previous volley are the
    /// same APPLICATION — a DoT's tick series, not fresh casts (ACT's
    /// sub-timer window, mirrored). Recast is measured between application
    /// starts; the trade-off is that true recasts under ~12 s can't be
    /// measured, and aren't timer material anyway.</summary>
    private static readonly TimeSpan ApplicationChain = TimeSpan.FromSeconds(12);

    public static List<MinedMob> MineZone(HistoryService history, string zone)
    {
        // ability key: (mob, ability) → per-fight (cast starts, that mob's
        // swiped windows) — windows travel with the fight for normalization.
        Dictionary<(string Mob, string Ability), List<(List<DateTimeOffset> Casts, List<(DateTimeOffset Start, DateTimeOffset End)> Swiped, double Fraction)>> castsPerFight =
            new();
        Dictionary<(string Mob, string Ability), (long Damage, int Hits, int Volleys, int Casts, HashSet<long> FightIds, bool Melee)> stats =
            new();

        foreach (var (summary, swings, enemies) in history.EnumerateArchivedFights(zone))
        {
            // Pre-scan: recast-debuff windows per mob (Traumatic Swipe hits
            // on the mob, each refreshing its duration; overlaps merged).
            Dictionary<string, (List<(DateTimeOffset, DateTimeOffset)> Windows, double Fraction)> swiped =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (var swing in swings)
            {
                if (!SpellTimerService.RecastDebuffs.TryGetValue(swing.Ability, out var debuff))
                    continue;
                if (!enemies.Contains(swing.Victim))
                    continue;
                if (!swiped.TryGetValue(swing.Victim, out var entry))
                    swiped[swing.Victim] = entry = ([], debuff.Fraction);
                entry.Windows.Add((swing.Time, swing.Time + debuff.Duration));
            }
            foreach (var entry in swiped.Values)
                MergeWindows(entry.Windows);
            // Group this fight's hostile swings by enemy attacker + ability.
            Dictionary<(string, string), List<Swing>> byAbility = new();
            foreach (var swing in swings)
            {
                if (swing.Category is not (SwingCategory.Melee or SwingCategory.NonMelee))
                    continue;
                if (!enemies.Contains(swing.Attacker))
                    continue;
                if (swing.Ability == Combatant.KillingAbility || swing.Ability.Length == 0)
                    continue;
                // Auto-attacks are never timer material — pure noise here.
                if (swing.Ability == Core.Grammar.EnglishGrammar.AutoAttackAbility)
                    continue;
                var key = (swing.Attacker, swing.Ability);
                if (!byAbility.TryGetValue(key, out var list))
                    byAbility[key] = list = [];
                list.Add(swing);
            }

            foreach (var ((mob, ability), hits) in byAbility)
            {
                // Level 1: hits → volleys. Level 2: volleys → applications
                // (a DoT's ticks chain into ONE application).
                hits.Sort((a, b) => a.Time.CompareTo(b.Time));
                List<DateTimeOffset> volleyStarts = [];
                var targets = 0;
                long damage = 0;
                var melee = true;
                DateTimeOffset? lastHit = null;
                foreach (var hit in hits)
                {
                    if (lastHit is null || hit.Time - lastHit.Value > CastCluster)
                        volleyStarts.Add(hit.Time);
                    lastHit = hit.Time;
                    targets++;
                    damage += Math.Max(0, hit.Damage.Number);
                    if (hit.Category != SwingCategory.Melee)
                        melee = false;
                }

                List<DateTimeOffset> applicationStarts = [];
                DateTimeOffset? previousVolley = null;
                foreach (var volley in volleyStarts)
                {
                    if (previousVolley is null || volley - previousVolley.Value > ApplicationChain)
                        applicationStarts.Add(volley);
                    previousVolley = volley;
                }

                var key = (mob, ability);
                if (!castsPerFight.TryGetValue(key, out var fights))
                    castsPerFight[key] = fights = [];
                var mobSwipes = swiped.TryGetValue(mob, out var sw) ? sw : ([], 0.5);
                fights.Add((applicationStarts, mobSwipes.Windows, mobSwipes.Fraction));
                var s = stats.TryGetValue(key, out var existing)
                    ? existing
                    : (0, 0, 0, 0, [], melee);
                s.Damage += damage;
                s.Hits += targets;
                s.Volleys += volleyStarts.Count;
                s.Casts += applicationStarts.Count;
                s.FightIds.Add(summary.Id);
                s.Melee &= melee;
                stats[key] = s;
            }
        }

        // Shape into mobs, computing recast stats from within-fight
        // intervals — raw, and normalized to base time by discounting the
        // swiped portions (rate 1/(1+f) while the debuff is on the mob).
        Dictionary<string, List<MinedAbility>> mobs = new(StringComparer.OrdinalIgnoreCase);
        foreach (var ((mob, ability), fights) in castsPerFight)
        {
            List<double> intervals = [];
            List<double> baseIntervals = [];
            double swipedTime = 0, totalTime = 0;
            foreach (var (castStarts, windows, fraction) in fights)
            {
                for (var i = 1; i < castStarts.Count; i++)
                {
                    var start = castStarts[i - 1];
                    var end = castStarts[i];
                    var interval = (end - start).TotalSeconds;
                    intervals.Add(interval);
                    var overlap = Overlap(start, end, windows);
                    baseIntervals.Add(interval - overlap + overlap / (1 + fraction));
                    swipedTime += overlap;
                    totalTime += interval;
                }
            }
            intervals.Sort();
            baseIntervals.Sort();
            var s = stats[(mob, ability)];
            var mined = new MinedAbility(
                zone, mob, ability,
                s.Casts, s.FightIds.Count,
                intervals.Count > 0 ? intervals[intervals.Count / 2] : null,
                intervals.Count > 0 ? intervals[0] : null,
                baseIntervals.Count > 0 ? baseIntervals[baseIntervals.Count / 2] : null,
                totalTime > 0 ? swipedTime / totalTime : 0,
                s.Damage,
                s.Volleys > 0 ? (double)s.Hits / s.Volleys : 0,
                s.Casts > 0 ? (double)s.Volleys / s.Casts : 0,
                s.Melee);
            if (!mobs.TryGetValue(mob, out var list))
                mobs[mob] = list = [];
            list.Add(mined);
        }

        return [.. mobs
            .Select(kv => new MinedMob(zone, kv.Key,
                [.. kv.Value.OrderByDescending(a => a.TotalDamage).Take(10)]))
            .OrderBy(m => m.Mob, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Merge overlapping/adjacent windows in place (sorted).</summary>
    private static void MergeWindows(List<(DateTimeOffset Start, DateTimeOffset End)> windows)
    {
        windows.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = 0;
        for (var i = 1; i < windows.Count; i++)
        {
            if (windows[i].Start <= windows[merged].End)
                windows[merged] = (windows[merged].Start,
                    windows[i].End > windows[merged].End ? windows[i].End : windows[merged].End);
            else
                windows[++merged] = windows[i];
        }
        if (windows.Count > merged + 1)
            windows.RemoveRange(merged + 1, windows.Count - merged - 1);
    }

    /// <summary>Seconds of [start, end] covered by the merged windows.</summary>
    private static double Overlap(DateTimeOffset start, DateTimeOffset end,
        List<(DateTimeOffset Start, DateTimeOffset End)> windows)
    {
        double covered = 0;
        foreach (var (ws, we) in windows)
        {
            var s = ws > start ? ws : start;
            var e = we < end ? we : end;
            if (e > s)
                covered += (e - s).TotalSeconds;
        }
        return covered;
    }
}
