using System.Text.Json;

namespace EQ2Parser.Core;

/// <summary>Shared serializer options for the embedded-resource loaders —
/// JsonSerializerOptions caches type metadata per instance, so ad-hoc
/// per-call instances rebuild it every time (CA1869).</summary>
internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
