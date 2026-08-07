namespace EQ2Parser.Core;

/// <summary>
/// UI-language resolution rule, kept in Core so it's unit-testable (the
/// WPF App project can't be referenced by the test suite). The contract:
/// an explicit user choice always wins; no choice follows the OS language
/// when we ship that dictionary, else English. A Russian Windows gets
/// Russian out of the box; a Croatian Windows gets English.
/// </summary>
public static class UiLanguage
{
    /// <summary>Resolve the language to load.</summary>
    /// <param name="requested">The persisted setting: a language code, or
    /// ""/null for "follow the OS".</param>
    /// <param name="osLanguage">The OS UI culture's two-letter code.</param>
    /// <param name="supported">Codes we ship dictionaries for.</param>
    public static string Resolve(string? requested, string osLanguage, IReadOnlyCollection<string> supported)
    {
        var code = string.IsNullOrEmpty(requested) ? osLanguage : requested;
        return supported.Contains(code) ? code : "en";
    }
}
