using System.Reflection.Metadata;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// Interface-localization integrity. The dictionaries live in the App
/// project (Localization/strings.{lang}.json); these tests reach them via
/// the repo tree so a missing key or a typo'd {loc:Tr …} reference fails
/// CI instead of silently rendering the raw key at runtime.
/// </summary>
public class LocalizationTests
{
    private static readonly string[] LanguageCodes = ["en", "de", "fr", "ru"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EQ2Parser.slnx")))
            dir = dir.Parent!;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    private static string AppDir() => Path.Combine(RepoRoot(), "src", "EQ2Parser.App");

    private static Dictionary<string, string> LoadLanguage(string code)
    {
        var path = Path.Combine(AppDir(), "Localization", $"strings.{code}.json");
        Assert.True(File.Exists(path), $"missing dictionary: {path}");
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        Assert.NotNull(parsed);
        return parsed;
    }

    [Fact]
    public void Every_Language_Has_Exactly_The_English_Key_Set()
    {
        var english = LoadLanguage("en");
        Assert.NotEmpty(english);
        foreach (var code in LanguageCodes.Skip(1))
        {
            var lang = LoadLanguage(code);
            var missing = english.Keys.Except(lang.Keys).ToList();
            var extra = lang.Keys.Except(english.Keys).ToList();
            Assert.True(missing.Count == 0, $"{code} missing keys: {string.Join(", ", missing.Take(10))}");
            Assert.True(extra.Count == 0, $"{code} extra keys: {string.Join(", ", extra.Take(10))}");
        }
    }

    [Fact]
    public void Placeholders_Match_English_In_Every_Language()
    {
        var english = LoadLanguage("en");
        foreach (var code in LanguageCodes.Skip(1))
        {
            var lang = LoadLanguage(code);
            foreach (var (key, en) in english)
            {
                if (!lang.TryGetValue(key, out var translated))
                    continue; // covered by the key-set test
                var enSlots = Placeholders(en);
                var trSlots = Placeholders(translated);
                Assert.True(enSlots.SetEquals(trSlots),
                    $"{code}:{key} placeholders {{{string.Join(",", trSlots)}}} != english {{{string.Join(",", enSlots)}}}");
            }
        }
    }

    [Fact]
    public void Every_Referenced_Key_Exists_In_English()
    {
        var english = LoadLanguage("en");
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var xaml in Directory.EnumerateFiles(AppDir(), "*.xaml", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(xaml), @"\{loc:Tr\s+([A-Za-z0-9_]+)\}"))
                referenced.Add(m.Groups[1].Value);
        }
        foreach (var cs in Directory.EnumerateFiles(AppDir(), "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(cs), @"Loc\.(?:Get|Format)\(\s*""([^""]+)"""))
                referenced.Add(m.Groups[1].Value);
        }

        Assert.NotEmpty(referenced);
        var unknown = referenced.Except(english.Keys).OrderBy(k => k).ToList();
        Assert.True(unknown.Count == 0,
            $"referenced but not in strings.en.json: {string.Join(", ", unknown.Take(15))}");
    }

    private static HashSet<string> Placeholders(string s) =>
        [.. Regex.Matches(s, @"\{(\d+)[^}]*\}").Select(m => m.Groups[1].Value)];

    private static readonly string[] BuildConfigs = ["Release", "Debug"];

    /// <summary>The v0.2.5 regression: MSBuild's AssignCulture read the
    /// ".de"/".en" filename segment as a culture suffix and routed every
    /// dictionary into satellite assemblies — the app shipped rendering
    /// raw keys. Reads the built dll's manifest via metadata (no load) and
    /// requires every dictionary in the MAIN assembly. Skips when the App
    /// hasn't been built (test-only runs).</summary>
    [Fact]
    public void Dictionaries_Are_Embedded_In_The_Built_App_Assembly()
    {
        var binRoot = Path.Combine(AppDir(), "bin");
        var dll = BuildConfigs
            .SelectMany(cfg =>
            {
                var dir = Path.Combine(binRoot, cfg);
                return Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir, "EQ2Parser.App.dll", SearchOption.AllDirectories)
                    : [];
            })
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (dll is null)
            return; // app never built in this checkout — nothing to verify

        using var stream = File.OpenRead(dll);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var md = pe.GetMetadataReader();
        var names = md.ManifestResources
            .Select(h => md.GetString(md.GetManifestResource(h).Name))
            .ToList();
        foreach (var code in LanguageCodes)
            Assert.Contains($"EQ2Parser.App.Localization.strings.{code}.json", names);
    }

    // ── OS-language defaulting (the out-of-box rule) ───────────────────────

    private static readonly string[] Supported = ["en", "de", "fr", "ru"];

    [Theory]
    // No explicit setting: follow the OS when we ship that language...
    [InlineData("", "ru", "ru")]
    [InlineData("", "de", "de")]
    [InlineData("", "fr", "fr")]
    [InlineData("", "en", "en")]
    [InlineData(null, "ru", "ru")]
    // ...and fall back to English when we don't (Croatian, Japanese, ...).
    [InlineData("", "hr", "en")]
    [InlineData("", "ja", "en")]
    // An explicit user choice always wins over the OS.
    [InlineData("de", "ru", "de")]
    [InlineData("en", "ru", "en")]
    // A stale/unknown persisted code degrades to English.
    [InlineData("xx", "ru", "en")]
    public void Os_Language_Defaults_Are_Honoured(string? requested, string os, string expected) =>
        Assert.Equal(expected, UiLanguage.Resolve(requested, os, Supported));
}
