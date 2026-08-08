using System.Text.RegularExpressions;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// Core-internal layering: Combat is the bottom layer — the encounter model
/// must never depend on the grammar, engine, or any higher module. (The
/// Core-never-references-WPF rule needs no test: Core targets plain
/// net10.0, so the compiler enforces it.) Source-level on purpose: the one
/// real violation this rule has had was a CONST reference
/// (Combatant → EnglishGrammar.AutoAttackAbility), and consts inline at
/// compile time — IL inspection is blind to exactly the case that matters.
/// </summary>
public class LayeringTests
{
    private static readonly string[] ForbiddenInCombat =
    [
        "Grammar", "Engine", "Correlation", "Analysis",
        "Triggers", "Upload", "History", "Logs", "Persistence",
    ];

    [Fact]
    public void Combat_Never_References_A_Higher_Core_Layer()
    {
        var combatDir = Path.Combine(FindRepoRoot(), "src", "EQ2Parser.Core", "Combat");
        Assert.True(Directory.Exists(combatDir), $"Combat source dir missing: {combatDir}");

        List<string> violations = [];
        foreach (var file in Directory.GetFiles(combatDir, "*.cs"))
        {
            var lineNumber = 0;
            foreach (var raw in File.ReadLines(file))
            {
                lineNumber++;
                // Comments may legitimately mention other layers.
                var code = raw;
                var comment = code.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0)
                    code = code[..comment];
                foreach (var layer in ForbiddenInCombat)
                {
                    if (Regex.IsMatch(code, $@"using EQ2Parser\.Core\.{layer}\b")
                        || Regex.IsMatch(code, $@"\b{layer}\."))
                        violations.Add($"{Path.GetFileName(file)}:{lineNumber} references {layer}: {raw.Trim()}");
                }
            }
        }
        Assert.True(violations.Count == 0,
            "Combat is the bottom Core layer and must not depend upward. "
            + "Move the shared symbol INTO Combat instead (as with Swing.AutoAttackAbility).\n"
            + string.Join("\n", violations));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "EQ2Parser.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "could not locate the repo root (EQ2Parser.slnx) above the test bin dir");
        return dir!;
    }
}
