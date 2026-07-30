using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQ2Parser.Core.Analysis;

/// <summary>
/// The embedded spell→class lookup (Resources/spell_classes.json, generated
/// by EQ2Lexicon's build_spell_classes.py from per-class census reference
/// characters across live + TLE servers). Base names are lower-cased with
/// trailing roman numerals stripped; a name absent from the map is, by
/// construction, not a scribable class ability (⇒ item proc signal).
/// </summary>
public sealed partial class SpellClassMap
{
    private readonly Dictionary<string, string[]> _map;

    private SpellClassMap(Dictionary<string, string[]> map) => _map = map;

    public int Count => _map.Count;

    public static SpellClassMap LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("EQ2Parser.Core.Resources.spell_classes.json")
            ?? throw new InvalidOperationException("spell_classes.json resource missing");
        var map = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)
            ?? throw new InvalidOperationException("spell_classes.json is empty");
        return new SpellClassMap(new Dictionary<string, string[]>(map, StringComparer.Ordinal));
    }

    /// <summary>For tests: build from an in-memory mapping.</summary>
    public static SpellClassMap FromDictionary(Dictionary<string, string[]> map) =>
        new(new Dictionary<string, string[]>(map, StringComparer.Ordinal));

    [GeneratedRegex(@"\s+[IVXLC]+$")]
    private static partial Regex TrailingRoman();

    public static string Normalize(string abilityName) =>
        TrailingRoman().Replace(abilityName.Trim(), "").ToLowerInvariant();

    /// <summary>Classes that can scribe this ability; empty = not a class ability.</summary>
    public IReadOnlyList<string> ClassesFor(string abilityName) =>
        _map.TryGetValue(Normalize(abilityName), out var classes) ? classes : [];
}
