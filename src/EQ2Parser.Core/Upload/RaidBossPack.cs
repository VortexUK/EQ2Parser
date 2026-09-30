using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQ2Parser.Core.Upload;

/// <summary>Wire shape of GET /api/zones/raid-bosses: a content-hash
/// version plus every curated raid-zone boss mob name, already normalised
/// server-side. Both nullable — System.Text.Json binds explicit nulls into
/// non-nullable members without complaint and the NRE would fire later.</summary>
public sealed class RaidBossPack
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("bosses")]
    public List<string>? Bosses { get; init; }

    /// <summary>Parse a pack body; null on malformed JSON or a body without
    /// a boss list (the caller keeps whatever it had).</summary>
    public static RaidBossPack? Parse(string json)
    {
        try
        {
            var pack = JsonSerializer.Deserialize<RaidBossPack>(json);
            return pack?.Bosses is null ? null : pack;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The boss names as a lookup set in the filter's key shape
    /// (re-normalised defensively — a pack built by an older server or a
    /// hand-edited cache still matches).</summary>
    public IReadOnlySet<string> ToSet()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in Bosses ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name))
                set.Add(RaidUploadFilter.NormaliseBossName(name));
        }
        return set;
    }
}
