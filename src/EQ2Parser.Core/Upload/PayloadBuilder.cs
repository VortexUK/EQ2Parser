using System.Security.Cryptography;
using System.Text;
using EQ2Parser.Core.Combat;

namespace EQ2Parser.Core.Upload;

/// <summary>
/// Maps a completed <see cref="Encounter"/> onto the EQ2Lexicon ingest
/// schema. The encid is a stable content hash so re-uploading the same
/// fight dedupes server-side (the site's UNIQUE(world, act_encid) rule).
/// </summary>
public static class PayloadBuilder
{
    public static LexiconPayload Build(
        Encounter encounter, string loggerServer, IReadOnlyList<string>? clientWarnings = null)
    {
        var allies = encounter.GetAllies();
        var allyKeys = new HashSet<string>(allies.Select(a => a.Key), StringComparer.Ordinal);
        var encounterSeconds = encounter.Duration.TotalSeconds;

        var combatants = new List<PayloadCombatant>();
        var damageTypes = new List<PayloadDamageType>();
        var attackTypes = new List<PayloadAttackType>();

        foreach (var combatant in encounter.Combatants.Values.OrderByDescending(c => c.Damage))
        {
            var isAlly = allyKeys.Contains(combatant.Key);
            var outgoingDamage = combatant.OutgoingBuckets.GetValueOrDefault(BucketConfig.OutgoingDamage)?.All;
            var healedOut = combatant.OutgoingBuckets.GetValueOrDefault(BucketConfig.HealedOut)?.All;
            var personalSeconds = combatant.Duration.TotalSeconds;
            // Victim-only combatants (an add that died without acting) have
            // sentinel MaxValue/MinValue windows — clamp to the fight, or
            // the site stores year-9999/year-1 garbage.
            var start = combatant.StartTime == DateTimeOffset.MaxValue ? encounter.StartTime : combatant.StartTime;
            var end = combatant.EndTime == DateTimeOffset.MinValue ? start : combatant.EndTime;

            combatants.Add(new PayloadCombatant
            {
                Name = combatant.Name,
                Ally = isAlly ? "T" : "F",
                StartTime = Ts(start),
                EndTime = Ts(end),
                Duration = (int)personalSeconds,
                Damage = combatant.Damage,
                Kills = combatant.GetKills(isAlly),
                Healed = combatant.Healed,
                CritHeals = healedOut?.CritHits ?? 0,
                Heals = healedOut?.SwingCount ?? 0,
                CureDispels = combatant.CureDispels,
                PowerDrain = combatant.PowerDamage,
                PowerReplenish = combatant.PowerReplenish,
                Dps = Rate(combatant.Damage, personalSeconds),
                EncDps = Rate(combatant.Damage, encounterSeconds),
                EncHps = Rate(combatant.Healed, encounterSeconds),
                Hits = outgoingDamage?.Hits ?? 0,
                CritHits = outgoingDamage?.CritHits ?? 0,
                Blocked = outgoingDamage?.Avoids ?? 0,
                Misses = outgoingDamage?.Misses ?? 0,
                Swings = outgoingDamage?.SwingCount ?? 0,
                HealsTaken = combatant.HealsTaken,
                DamageTaken = combatant.DamageTaken,
                Deaths = combatant.Deaths,
                ToHit = ToHit(outgoingDamage),
            });

            foreach (var (bucketName, bucket) in combatant.OutgoingBuckets)
            {
                if (bucketName == BucketConfig.AllOutgoingRef)
                    continue;
                var all = bucket.All;
                if (all.Swings.Count == 0)
                    continue;
                damageTypes.Add(new PayloadDamageType
                {
                    Combatant = combatant.Name,
                    Type = bucketName,
                    StartTime = Ts(all.StartTime),
                    EndTime = Ts(all.EndTime),
                    Damage = all.Damage,
                    EncDps = Rate(all.Damage, encounterSeconds),
                    Dps = Rate(all.Damage, Math.Max(1, (all.EndTime - all.StartTime).TotalSeconds)),
                    CritPerc = all.Hits > 0 ? Math.Round(100.0 * all.CritHits / all.Hits, 1) : 0,
                    Median = all.Median,
                    MinHit = all.MinHit,
                    MaxHit = all.MaxHit,
                    Hits = all.Hits,
                    CritHits = all.CritHits,
                    Blocked = all.Avoids,
                    Misses = all.Misses,
                    Swings = all.SwingCount,
                });
            }

            // attack_types come from the REFERENCE bucket — every outgoing
            // swing exactly once per ability. Building them per category
            // bucket shipped duplicates (a melee swing feeds Outgoing Damage
            // AND Auto-Attack (Out)) and tripped the site's
            // UNIQUE(combatant_id, swing_type, attack_name) with a 500.
            if (combatant.OutgoingBuckets.GetValueOrDefault(BucketConfig.AllOutgoingRef) is { } reference)
            {
                foreach (var (abilityName, ability) in reference.Abilities)
                {
                    if (abilityName == Bucket.AllAbility || ability.Swings.Count == 0)
                        continue;
                    attackTypes.Add(new PayloadAttackType
                    {
                        Attacker = combatant.Name,
                        Type = abilityName,
                        SwingType = (int)ability.Swings[0].Category,
                        Damage = ability.Damage,
                        EncDps = Rate(ability.Damage, encounterSeconds),
                        // ACT's per-attacktype DPS: damage over the type's own
                        // first-to-last-swing window, min 1 s (a single-swing
                        // window is 0 s and would divide away the damage).
                        Dps = Rate(ability.Damage, Math.Max(1, (ability.EndTime - ability.StartTime).TotalSeconds)),
                        CritPerc = ability.Hits > 0 ? Math.Round(100.0 * ability.CritHits / ability.Hits, 1) : 0,
                        Median = ability.Median,
                        MinHit = ability.MinHit,
                        MaxHit = ability.MaxHit,
                        Hits = ability.Hits,
                        CritHits = ability.CritHits,
                        Blocked = ability.Avoids,
                        Misses = ability.Misses,
                        Swings = ability.SwingCount,
                    });
                }
            }
        }

        var alliedDeaths = allies.Where(a => Swing.LooksLikePlayer(a.Name)).Sum(a => a.Deaths);
        var alliedKills = allies.Sum(a => a.GetKills(isAlly: true));

        return new LexiconPayload
        {
            LoggerName = encounter.OwnerName,
            LoggerServer = loggerServer,
            Encounter = new PayloadEncounter
            {
                EncId = ComputeEncId(encounter),
                Title = encounter.Title,
                Zone = encounter.Zone.Length > 0 ? encounter.Zone : null,
                StartTime = Ts(encounter.StartTime),
                EndTime = Ts(encounter.EndTime),
                Duration = (int)encounterSeconds,
                Damage = encounter.Damage,
                EncDps = encounter.EncDps,
                Kills = alliedKills,
                Deaths = alliedDeaths,
                Success = (int)encounter.GetSuccessLevel(),
            },
            Combatants = combatants,
            DamageTypes = damageTypes,
            AttackTypes = attackTypes,
            ClientWarnings = clientWarnings is { Count: > 0 } ? [.. clientWarnings] : null,
        };
    }

    /// <summary>Stable 8-hex content id: same fight → same id → server-side
    /// dedupe on re-upload (UNIQUE(world, act_encid)).</summary>
    public static string ComputeEncId(Encounter encounter)
    {
        var seed = new StringBuilder()
            .Append(encounter.OwnerName).Append('|')
            .Append(encounter.Zone).Append('|')
            .Append(encounter.Title).Append('|')
            .Append(encounter.StartTime.ToUnixTimeSeconds()).Append('|')
            .Append(encounter.Combatants.Count);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString()));
        return Convert.ToHexStringLower(hash)[..8];
    }

    private static string Ts(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static double Rate(long amount, double seconds) =>
        seconds > 0 ? Math.Round(amount / seconds, 2) : 0;

    private static double ToHit(AbilityStats? stats)
    {
        if (stats is null || stats.SwingCount == 0)
            return 0;
        return Math.Round(100.0 * stats.Hits / stats.SwingCount, 2);
    }
}
