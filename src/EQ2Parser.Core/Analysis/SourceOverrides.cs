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
    /// to (null/empty = any class), and the forced source.</summary>
    private sealed record Rule(HashSet<string>? Classes, AbilitySource Source);

    private sealed record RuleFile(List<RuleEntry>? Overrides);

    private sealed record RuleEntry(string? Ability, string[]? Classes, string? Source, string? Note);

    private readonly Dictionary<string, List<Rule>> _rules = new(StringComparer.Ordinal);

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
            if (entry.Ability is not { Length: > 0 } ability || entry.Source is not { Length: > 0 } source)
                continue;
            if (!Enum.TryParse<AbilitySource>(source, ignoreCase: true, out var parsed))
                continue;
            var classes = entry.Classes is { Length: > 0 }
                ? new HashSet<string>(entry.Classes, StringComparer.OrdinalIgnoreCase)
                : null;
            var key = SpellClassMap.Normalize(ability);
            if (!_rules.TryGetValue(key, out var list))
                _rules[key] = list = [];
            if (prepend)
                list.Insert(0, new Rule(classes, parsed));
            else
                list.Add(new Rule(classes, parsed));
        }
    }

    /// <summary>First matching rule for this ability + detected class, if
    /// any. Class-scoped rules need a detected class to match; unscoped
    /// rules always match the ability.</summary>
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
        return false;
    }
}
