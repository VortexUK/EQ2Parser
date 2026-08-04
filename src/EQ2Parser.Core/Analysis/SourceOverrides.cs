using System.Reflection;
using System.Text.Json;

namespace EQ2Parser.Core.Analysis;

/// <summary>
/// Curated ability-source corrections consulted BEFORE the census-derived
/// spell map — the place for knowledge the map cannot represent (e.g.
/// "Divine Smash" is a cleric hammer temp-pet ability that logs under the
/// owner's name: census never lists it, so the map called it an item proc).
///
/// Two layers: the embedded Resources/source_overrides.json (curated
/// in-repo), plus an optional user file merged ON TOP (its rules win) so a
/// mislabel can be hot-fixed locally without a release, then promoted into
/// the embedded file. A malformed user file is skipped — the reason is kept
/// in <see cref="LoadError"/> rather than silently vanishing.
/// </summary>
public sealed class SourceOverrides
{
    /// <summary>One correction: ability (normalized), the classes it applies
    /// to (null/empty = any class), the forced source, and whether the
    /// ability is a renameable summoner-pet's kit ("pet": true) — on a
    /// detected class OUTSIDE the rule's classes that tags as
    /// <see cref="AbilitySource.Pet"/>, the renamed-pet padding signature.</summary>
    private sealed record Rule(HashSet<string>? Classes, AbilitySource Source, bool Pet);

    private sealed record RuleFile(List<RuleEntry>? Overrides);

    private sealed record RuleEntry(
        string? Ability, string[]? Classes, string? Source, string? Note, string[]? GrantedBy, bool? Pet);

    private readonly Dictionary<string, List<Rule>> _rules = new(StringComparer.Ordinal);

    /// <summary>ability (normalized) → curated granting classes for the
    /// raid-buff attributor — for the cases neither map layer states
    /// truthfully. First-merged (user file) wins.</summary>
    private readonly Dictionary<string, string[]> _grantedBy = new(StringComparer.Ordinal);

    /// <summary>Why the last MergeFile was skipped, or null when it loaded
    /// (or no file was present). Surfaced so a user edit that fails to parse
    /// is diagnosable instead of mysteriously doing nothing.</summary>
    public string? LoadError { get; private set; }

    public static SourceOverrides Empty { get; } = new();

    public static SourceOverrides LoadEmbedded()
    {
        var overrides = new SourceOverrides();
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("EQ2Parser.Core.Resources.source_overrides.json")
            ?? throw new InvalidOperationException("source_overrides.json resource missing");
        overrides.Merge(stream);
        return overrides;
    }

    /// <summary>Merge a user override file on top (later rules win). Missing
    /// file is a no-op; a malformed one is skipped with LoadError set.</summary>
    public SourceOverrides MergeFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                Merge(stream, prepend: true);
                LoadError = null;
            }
        }
        catch (Exception ex)
        {
            LoadError = $"{Path.GetFileName(path)}: {ex.Message}";
        }
        return this;
    }

    /// <summary>For tests: build from JSON text.</summary>
    public static SourceOverrides FromJson(string json)
    {
        var overrides = new SourceOverrides();
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        overrides.Merge(ms);
        return overrides;
    }

    private void Merge(Stream stream, bool prepend = false)
    {
        var file = JsonSerializer.Deserialize<RuleFile>(
            stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        foreach (var entry in file?.Overrides ?? [])
        {
            if (entry.Ability is not { Length: > 0 } ability)
                continue;
            var key = SpellClassMap.Normalize(ability);
            if (entry.GrantedBy is { Length: > 0 } granted
                && (prepend || !_grantedBy.ContainsKey(key)))
            {
                _grantedBy[key] = granted;
            }
            if (entry.Source is not { Length: > 0 } source
                || !Enum.TryParse<AbilitySource>(source, ignoreCase: true, out var parsed))
                continue;
            var classes = entry.Classes is { Length: > 0 }
                ? new HashSet<string>(entry.Classes, StringComparer.OrdinalIgnoreCase)
                : null;
            if (!_rules.TryGetValue(key, out var list))
                _rules[key] = list = [];
            var rule = new Rule(classes, parsed, entry.Pet == true && classes is not null);
            if (prepend)
                list.Insert(0, rule);
            else
                list.Add(rule);
        }
    }

    /// <summary>Curated granting classes for an ability, or null when no
    /// override exists (the attributor then asks the map).</summary>
    public IReadOnlyList<string>? GrantedByFor(string abilityName) =>
        _grantedBy.TryGetValue(SpellClassMap.Normalize(abilityName), out var granted) ? granted : null;

    /// <summary>First matching rule for this ability + detected class, if
    /// any. Class-scoped rules need a detected class to match; unscoped
    /// rules always match the ability. When no rule matches but the ability
    /// is a summoner-pet kit ("pet": true) and the detected class is OUTSIDE
    /// the owning classes, resolves to <see cref="AbilitySource.Pet"/> — a
    /// renamed pet merging its damage into this player (an undetected class
    /// stays on the map fallback; no claim without evidence).</summary>
    public bool TryResolve(string abilityName, string? detectedClass, out AbilitySource source)
    {
        source = default;
        if (_rules.Count == 0 || !_rules.TryGetValue(SpellClassMap.Normalize(abilityName), out var rules))
            return false;
        foreach (var rule in rules)
        {
            if (rule.Classes is null || (detectedClass is not null && rule.Classes.Contains(detectedClass)))
            {
                source = rule.Source;
                return true;
            }
        }
        if (detectedClass is not null && rules.Any(r => r.Pet))
        {
            source = AbilitySource.Pet;
            return true;
        }
        return false;
    }
}
