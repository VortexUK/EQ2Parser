using EQ2Parser.Core.Combat;
using EQ2Parser.Core.History;

namespace EQ2Parser.App.Services;

/// <summary>One enemy ability as observed across archived fights: how often
/// it was cast, its measured recast rhythm, and what it did — the ground
/// truth spell timers get calculated from.</summary>
public sealed record MinedAbility(
    string Zone,
    string Mob,
    string Ability,
    int Casts,
    int Fights,
    double? MedianIntervalSeconds,
    double? MinIntervalSeconds,
    long TotalDamage,
    double AvgTargets,
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
    /// <summary>Hits within this window of the previous hit belong to the
    /// same cast (multi-target landing + a tick of lag).</summary>
    private static readonly TimeSpan CastCluster = TimeSpan.FromSeconds(2);

    public static List<MinedMob> MineZone(HistoryService history, string zone)
    {
        // ability key: (mob, ability) → per-fight cast start lists
        Dictionary<(string Mob, string Ability), List<List<DateTimeOffset>>> castsPerFight =
            new();
        Dictionary<(string Mob, string Ability), (long Damage, int Hits, int Casts, HashSet<long> FightIds, bool Melee)> stats =
            new();

        foreach (var (summary, swings, enemies) in history.EnumerateArchivedFights(zone))
        {
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
                // Cluster hits into casts.
                hits.Sort((a, b) => a.Time.CompareTo(b.Time));
                List<DateTimeOffset> castStarts = [];
                var castTargets = 0;
                long damage = 0;
                var melee = true;
                DateTimeOffset? lastHit = null;
                foreach (var hit in hits)
                {
                    if (lastHit is null || hit.Time - lastHit.Value > CastCluster)
                        castStarts.Add(hit.Time);
                    lastHit = hit.Time;
                    castTargets++;
                    damage += Math.Max(0, hit.Damage.Number);
                    if (hit.Category != SwingCategory.Melee)
                        melee = false;
                }

                var key = (mob, ability);
                if (!castsPerFight.TryGetValue(key, out var fights))
                    castsPerFight[key] = fights = [];
                fights.Add(castStarts);
                var s = stats.TryGetValue(key, out var existing)
                    ? existing
                    : (0, 0, 0, [], melee);
                s.Damage += damage;
                s.Hits += castTargets;
                s.Casts += castStarts.Count;
                s.FightIds.Add(summary.Id);
                s.Melee &= melee;
                stats[key] = s;
            }
        }

        // Shape into mobs, computing recast stats from within-fight intervals.
        Dictionary<string, List<MinedAbility>> mobs = new(StringComparer.OrdinalIgnoreCase);
        foreach (var ((mob, ability), fights) in castsPerFight)
        {
            List<double> intervals = [];
            foreach (var castStarts in fights)
            {
                for (var i = 1; i < castStarts.Count; i++)
                    intervals.Add((castStarts[i] - castStarts[i - 1]).TotalSeconds);
            }
            intervals.Sort();
            var s = stats[(mob, ability)];
            var mined = new MinedAbility(
                zone, mob, ability,
                s.Casts, s.FightIds.Count,
                intervals.Count > 0 ? intervals[intervals.Count / 2] : null,
                intervals.Count > 0 ? intervals[0] : null,
                s.Damage,
                s.Casts > 0 ? (double)s.Hits / s.Casts : 0,
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
}
