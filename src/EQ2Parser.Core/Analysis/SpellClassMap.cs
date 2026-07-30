using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQ2Parser.Core.Analysis;

/// <summary>
/// The embedded spell→class lookup (Resources/spell_classes.json, generated
/// by EQ2Lexicon's build_spell_classes.py from per-class census reference
/// characters across live + TLE servers). Two layers:
///
/// - "spells": knowledge-book names from class-channel scrolls. Safe for
///   class VOTING — a character casting these is that class.
/// - "effects": triggered-effect names mined from spell effect text ("Holy
///   Intercession V" logs as its effect "Divine Prayer"). Safe for source
///   TAGGING only — granted procs (Aria of Magic's "Precise Note") fire on
///   the recipient's own casts, so they identify the GRANTING class, not
///   the caster.
///
/// Base names are lower-cased with trailing roman numerals stripped; a name
/// absent from BOTH layers is, by construction, not a class ability
/// (⇒ item proc signal).
/// </summary>
public sealed partial class SpellClassMap
{
    private readonly Dictionary<string, string[]> _spells;
    private readonly Dictionary<string, string[]> _effects;

    private SpellClassMap(Dictionary<string, string[]> spells, Dictionary<string, string[]> effects)
    {
        _spells = spells;
        _effects = effects;
    }

    public int Count => _spells.Count;

    private sealed record MapFile(Dictionary<string, string[]> Spells, Dictionary<string, string[]> Effects);

    public static SpellClassMap LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("EQ2Parser.Core.Resources.spell_classes.json")
            ?? throw new InvalidOperationException("spell_classes.json resource missing");
        var file = JsonSerializer.Deserialize<MapFile>(
                stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("spell_classes.json is empty");
        return new SpellClassMap(
            new Dictionary<string, string[]>(file.Spells, StringComparer.Ordinal),
            new Dictionary<string, string[]>(file.Effects ?? [], StringComparer.Ordinal));
    }

    /// <summary>For tests: build from in-memory mappings (voting layer, plus
    /// an optional tag-only effects layer).</summary>
    public static SpellClassMap FromDictionary(
        Dictionary<string, string[]> spells, Dictionary<string, string[]>? effects = null) =>
        new(
            new Dictionary<string, string[]>(spells, StringComparer.Ordinal),
            new Dictionary<string, string[]>(effects ?? [], StringComparer.Ordinal));

    [GeneratedRegex(@"\s+[IVXLC]+$")]
    private static partial Regex TrailingRoman();

    public static string Normalize(string abilityName) =>
        TrailingRoman().Replace(abilityName.Trim(), "").ToLowerInvariant();

    /// <summary>Voting lookup — knowledge-book scroll names only.</summary>
    public IReadOnlyList<string> VotingClassesFor(string abilityName) =>
        _spells.TryGetValue(Normalize(abilityName), out var classes) ? classes : [];

    /// <summary>Tagging lookup — scroll names plus triggered-effect names.
    /// Empty = not a class ability at all (item proc signal).</summary>
    public IReadOnlyList<string> ClassesFor(string abilityName)
    {
        var key = Normalize(abilityName);
        if (!_spells.TryGetValue(key, out var scroll))
            return _effects.TryGetValue(key, out var effect) ? effect : [];
        if (!_effects.TryGetValue(key, out var extra))
            return scroll;
        return scroll.Union(extra, StringComparer.Ordinal).ToArray();
    }
}
