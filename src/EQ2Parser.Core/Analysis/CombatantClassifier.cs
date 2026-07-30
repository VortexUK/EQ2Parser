using System.Text.RegularExpressions;
using EQ2Parser.Core.Combat;

namespace EQ2Parser.Core.Analysis;

/// <summary>What a combatant is, for display and post-processing.</summary>
public enum CombatantKind
{
    Player,
    /// <summary>Pet / NPC helper / unresolved straggler (the same bucket the
    /// EQ2Lexicon pipeline persists as is_player = false).</summary>
    Pet,
    Enemy,
}

/// <summary>One combatant's verdict: kind, resolved pet owner (possessive
/// names only), and the class detection (computed for every kind — enemies
/// genuinely cast class kits too).</summary>
public sealed record CombatantTag(CombatantKind Kind, string? PetOwner, ClassDetection Class);

/// <summary>
/// Port of EQ2Lexicon's pet-detection pipeline
/// (backend/server/parses/pet_detection.py) composed with class detection.
/// Ally pipeline: empty/"Unknown" ⇒ pet; multi-word name ⇒ pet; EQ2
/// auto-pet name ⇒ pet; any mapped ability ⇒ player (replaces the site's
/// Census cls signal); survivors are unconfirmed and bucket-fill decides.
/// Bucket-fill uses the site's zone-free "other" rules (ally count ≤6 no-op,
/// 7-10 fill to 6, ≥11 treat as raid: fill to 24 + trim overflow), promoting
/// by EncDPS+EncHPS descending, demoting ascending, name tiebreak.
/// </summary>
public sealed partial class CombatantClassifier(ClassIdentifier identifier)
{
    // Verbatim port of the site's EQ2_PET_PATTERN — the [gkjxzv] stem +
    // optional middle syllable + common ending EQ2's pet-naming produces.
    [GeneratedRegex(
        @"^(?:[gkjxzv]i|[gkjxzv]e|[gkjxzv]a|[gkjxzv]o|je|jo|ja|ze|zo|ke|gi)(?:ba|be|bo|na|ne|ti|an)?(?:b|bn|ber|bekn|btik|tik|ntik|ner|sn|kn|n)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AutoPetName();

    /// <summary>Safety net for observed auto-pet names the regex misses.</summary>
    private static readonly HashSet<string> KnownPetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Gibab", "Zosn", "Kebn", "Zebekn", "Jentik",
    };

    public ClassIdentifier Identifier { get; } = identifier;

    /// <summary>Name-shape stages 2-4 of the site pipeline.</summary>
    public static bool IsPetName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed == "Unknown")
            return true;
        if (trimmed.Contains(' '))
            return true;
        return KnownPetNames.Contains(trimmed) || AutoPetName().IsMatch(trimmed);
    }

    /// <summary>Tag every combatant in the encounter, keyed by combatant key.</summary>
    public IReadOnlyDictionary<string, CombatantTag> Classify(Encounter encounter)
    {
        var allies = encounter.GetAllies();
        var allyKeys = allies.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        var tags = new Dictionary<string, CombatantTag>(StringComparer.Ordinal);

        var confirmed = new List<(Combatant Combatant, ClassDetection Class)>();
        var unconfirmed = new List<(Combatant Combatant, ClassDetection Class)>();
        foreach (var combatant in encounter.Combatants.Values)
        {
            var detection = Identifier.Detect(combatant);
            if (!allyKeys.Contains(combatant.Key))
            {
                tags[combatant.Key] = new CombatantTag(CombatantKind.Enemy, null, detection);
            }
            else if (IsPetName(combatant.Name))
            {
                tags[combatant.Key] = new CombatantTag(
                    CombatantKind.Pet, ResolveOwner(combatant.Name, encounter), detection);
            }
            else if (detection.MappedAbilities > 0)
            {
                confirmed.Add((combatant, detection));
            }
            else
            {
                unconfirmed.Add((combatant, detection));
            }
        }

        // Bucket-fill — the site's size-inferred "other" rules.
        var total = allies.Count;
        int? target = total <= 6 ? null : total <= 10 ? 6 : 24;

        double Contribution(Combatant c) => encounter.EncDpsOf(c) + encounter.EncHpsOf(c);

        if (target is int cap)
        {
            if (confirmed.Count < cap && unconfirmed.Count > 0)
            {
                unconfirmed.Sort((a, b) =>
                {
                    var byRate = Contribution(b.Combatant).CompareTo(Contribution(a.Combatant));
                    return byRate != 0 ? byRate : string.CompareOrdinal(a.Combatant.Name, b.Combatant.Name);
                });
                var promote = Math.Min(cap - confirmed.Count, unconfirmed.Count);
                confirmed.AddRange(unconfirmed.Take(promote));
                unconfirmed.RemoveRange(0, promote);
            }
            if (confirmed.Count > cap)
            {
                confirmed.Sort((a, b) =>
                {
                    var byRate = Contribution(a.Combatant).CompareTo(Contribution(b.Combatant));
                    return byRate != 0 ? byRate : string.CompareOrdinal(a.Combatant.Name, b.Combatant.Name);
                });
                var demote = confirmed.Count - cap;
                unconfirmed.AddRange(confirmed.Take(demote));
                confirmed.RemoveRange(0, demote);
            }
        }

        foreach (var (combatant, detection) in confirmed)
            tags[combatant.Key] = new CombatantTag(CombatantKind.Player, null, detection);
        foreach (var (combatant, detection) in unconfirmed)
            tags[combatant.Key] = new CombatantTag(CombatantKind.Pet, null, detection);
        return tags;
    }

    /// <summary>"Broomm's attack hawk" → Broomm, when the owner is present
    /// in the encounter. Auto-named pets carry no owner in the name.</summary>
    private static string? ResolveOwner(string petName, Encounter encounter)
    {
        var idx = petName.IndexOf("'s ", StringComparison.Ordinal);
        if (idx <= 0)
            return null;
        var owner = petName[..idx];
        return encounter.Combatants.TryGetValue(owner.ToUpperInvariant(), out var combatant)
            ? combatant.Name
            : null;
    }
}
