namespace EQ2Parser.Core.Analysis;

/// <summary>
/// Reattributes raid-granted proc damage to the class member(s) who granted
/// the buff. INFERENCE ONLY, by necessity: verified against 685MB of raid
/// logs (2026-08) that granted-buff casts write NOTHING to third-person
/// logs — Cacophony of Blades produces no cast emote and no application
/// line, and its Blade Chime procs credit the beneficiary. So: the map's
/// effects layer names the granting class, and the granter is whichever
/// detected ally is that class — one match is exact, several split evenly
/// (flagged estimated), none leaves the damage unattributed rather than
/// guessed. View-only: uploads and stored data are never rewritten.
/// </summary>
public sealed class RaidBuffAttributor(SpellClassMap map, SourceOverrides? overrides = null)
{
    private readonly SourceOverrides _overrides = overrides ?? SourceOverrides.Empty;

    /// <summary>One raid-granted ability's attribution verdict.</summary>
    /// <param name="Ability">Proc/effect name as logged.</param>
    /// <param name="Damage">Raid-wide damage done under that name.</param>
    /// <param name="GrantingClasses">Classes able to grant it (from the map).</param>
    /// <param name="Granters">Detected allies of a granting class — empty
    /// means unattributed (no such class detected in the fight).</param>
    /// <param name="Estimated">True when several granters share the class:
    /// the split is an even-share estimate, not knowledge.</param>
    public sealed record Credit(
        string Ability, long Damage,
        IReadOnlyList<string> GrantingClasses,
        IReadOnlyList<string> Granters,
        bool Estimated);

    /// <summary>Attribute each raid-sourced ability against the fight's
    /// detected ally classes.</summary>
    public IReadOnlyList<Credit> Attribute(
        IReadOnlyList<(string Ability, long Damage)> raidSourced,
        IReadOnlyDictionary<string, string> classByAlly)
    {
        List<Credit> credits = [];
        foreach (var (ability, damage) in raidSourced)
        {
            // Curated grantedBy overrides outrank the map; otherwise the
            // effects-first lookup (see GrantingClassesFor for why).
            var granting = _overrides.GrantedByFor(ability) ?? map.GrantingClassesFor(ability);
            var granters = classByAlly
                .Where(kv => granting.Contains(kv.Value, StringComparer.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            credits.Add(new Credit(ability, damage, granting, granters, granters.Length > 1));
        }
        return credits;
    }

    /// <summary>Damage credited per granter — an even share when several
    /// members of the granting class are present.</summary>
    public static Dictionary<string, long> CreditByGranter(IReadOnlyList<Credit> credits)
    {
        Dictionary<string, long> byGranter = new(StringComparer.OrdinalIgnoreCase);
        foreach (var credit in credits)
        {
            if (credit.Granters.Count == 0)
                continue;
            var share = credit.Damage / credit.Granters.Count;
            foreach (var granter in credit.Granters)
                byGranter[granter] = byGranter.GetValueOrDefault(granter) + share;
        }
        return byGranter;
    }
}
