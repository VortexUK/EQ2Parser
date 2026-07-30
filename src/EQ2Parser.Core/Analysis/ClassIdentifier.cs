using EQ2Parser.Core.Combat;

namespace EQ2Parser.Core.Analysis;

/// <summary>Where a combatant's ability came from.</summary>
public enum AbilitySource
{
    /// <summary>Their own class kit.</summary>
    Class,
    /// <summary>Another class's spell appearing in their outgoing list — a
    /// granted raid buff (e.g. Perfection of the Maestro on a Templar).</summary>
    Raid,
    /// <summary>Not a scribable class ability — item/adorn proc.</summary>
    Item,
    /// <summary>Auto-attack / system pseudo-abilities.</summary>
    System,
}

/// <summary>The verdict for one combatant.</summary>
/// <param name="ClassName">Winner, or null when nothing mapped.</param>
/// <param name="Confidence">Winner votes / mapped ability names (0..1).</param>
/// <param name="MappedAbilities">Distinct ability names found in the map.</param>
/// <param name="TotalAbilities">Distinct ability names considered.</param>
public sealed record ClassDetection(string? ClassName, double Confidence, int MappedAbilities, int TotalAbilities);

/// <summary>
/// Detects a combatant's class from the abilities THEY cast (outgoing swings
/// are inherently "obviously them" — buffs cast on them by others never
/// appear here), by majority vote over the spell→class map. Also tags each
/// ability with its source (class kit / granted raid buff / item proc).
/// </summary>
public sealed class ClassIdentifier(SpellClassMap map)
{
    private static readonly HashSet<string> SystemAbilities = new(StringComparer.Ordinal)
    {
        Grammar.EnglishGrammar.AutoAttackAbility,
        Combatant.KillingAbility,
    };

    public SpellClassMap Map { get; } = map;

    /// <summary>Distinct non-system ability names this combatant used.</summary>
    public static IReadOnlyList<string> CastAbilities(Combatant combatant)
    {
        if (!combatant.OutgoingBuckets.TryGetValue(BucketConfig.AllOutgoingRef, out var reference))
            return [];
        return reference.Abilities.Keys
            .Where(name => name != Bucket.AllAbility && !SystemAbilities.Contains(name))
            .ToArray();
    }

    public ClassDetection Detect(Combatant combatant)
    {
        var abilities = CastAbilities(combatant);
        var votes = new Dictionary<string, int>(StringComparer.Ordinal);
        var mapped = 0;
        foreach (var ability in abilities)
        {
            var classes = Map.ClassesFor(ability);
            if (classes.Count == 0)
                continue;
            mapped++;
            foreach (var cls in classes)
                votes[cls] = votes.GetValueOrDefault(cls) + 1;
        }
        if (mapped == 0)
            return new ClassDetection(null, 0, 0, abilities.Count);

        // A single stray vote is not evidence — e.g. an EoF Conjuror whose
        // only MAPPED ability was an item-granted "Breeze" (Illusionist) got
        // a confident wrong verdict, because pre-Sentinel's-Fate eras name
        // each spell tier uniquely and census never carries those names.
        // Require the winner to be corroborated by a second ability.
        var winner = votes.MaxBy(kv => kv.Value);
        if (winner.Value < 2)
            return new ClassDetection(null, 0, mapped, abilities.Count);

        return new ClassDetection(
            winner.Key,
            (double)winner.Value / mapped,
            mapped,
            abilities.Count);
    }

    /// <summary>Tag one ability for a combatant with a known/detected class.</summary>
    public AbilitySource ClassifySource(string abilityName, string? detectedClass)
    {
        if (SystemAbilities.Contains(abilityName))
            return AbilitySource.System;
        var classes = Map.ClassesFor(abilityName);
        if (classes.Count == 0)
            return AbilitySource.Item;
        if (detectedClass is not null && classes.Contains(detectedClass))
            return AbilitySource.Class;
        return AbilitySource.Raid;
    }
}
