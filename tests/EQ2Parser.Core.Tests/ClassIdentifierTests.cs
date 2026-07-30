using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Logs;
using Xunit.Abstractions;

namespace EQ2Parser.Core.Tests;

public class ClassIdentifierTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    private static readonly SpellClassMap Fixture = SpellClassMap.FromDictionary(
        new()
        {
            ["divine strike"] = ["Templar"],
            ["reverence"] = ["Templar"],
            ["holy strike"] = ["Paladin"],
            ["perfection of the maestro"] = ["Troubador"],
            ["bloodletter"] = ["Defiler", "Mystic"],
        },
        effects: new()
        {
            ["precise note"] = ["Troubador"],
            ["divine prayer"] = ["Templar"],
        });

    [Fact]
    public void Majority_Vote_Detects_The_Class()
    {
        var engine = new ParserEngine("log", "Menlu");
        Assert.True(engine.SetEncounter(T0, "Menlu", "a gnoll"));
        engine.AddSwing(SwingCategory.NonMelee, false, "None", "Menlu", "Divine Strike III", 100, T0, "a gnoll", "divine");
        engine.AddSwing(SwingCategory.Healing, false, "None", "Menlu", "Reverence", 50, T0, "Menlu", "heal");
        engine.AddSwing(SwingCategory.NonMelee, false, "None", "Menlu", "Perfection of the Maestro", 200, T0, "a gnoll", "magic"); // granted
        engine.AddSwing(SwingCategory.NonMelee, false, "None", "Menlu", "Wildfire", 300, T0, "a gnoll", "heat"); // proc, unmapped
        engine.AddSwing(SwingCategory.NonMelee, false, "None", "Menlu", "Precise Note", 150, T0, "a gnoll", "mental"); // granted effect proc
        engine.EndCombat();

        var identifier = new ClassIdentifier(Fixture);
        var combatant = engine.History[^1].Combatants["MENLU"];
        var detection = identifier.Detect(combatant);

        // Precise Note is effects-layer: it must NOT vote (it identifies the
        // granting Troubador, not the caster) and not count as mapped.
        Assert.Equal("Templar", detection.ClassName);
        Assert.Equal(2.0 / 3, detection.Confidence, precision: 5); // 2 Templar of 3 mapped
        Assert.Equal(3, detection.MappedAbilities);
        Assert.Equal(5, detection.TotalAbilities);

        // Source tagging (uses both layers).
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Divine Strike III", "Templar"));
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Divine Prayer", "Templar"));
        Assert.Equal(AbilitySource.Raid, identifier.ClassifySource("Perfection of the Maestro", "Templar"));
        Assert.Equal(AbilitySource.Raid, identifier.ClassifySource("Precise Note", "Templar"));
        Assert.Equal(AbilitySource.Item, identifier.ClassifySource("Wildfire", "Templar"));
        Assert.Equal(AbilitySource.System, identifier.ClassifySource(Grammar.EnglishGrammar.AutoAttackAbility, "Templar"));
    }

    [Fact]
    public void Single_Vote_Is_Not_A_Verdict()
    {
        // One stray mapped ability (e.g. an item-granted Breeze on an
        // era-named Conjuror kit) must not produce a confident verdict.
        var engine = new ParserEngine("log", "Menlu");
        Assert.True(engine.SetEncounter(T0, "Senn", "a gnoll"));
        engine.AddSwing(SwingCategory.NonMelee, false, "None", "Senn", "Reverence", 50, T0, "Senn", "heal");
        engine.AddSwing(SwingCategory.NonMelee, false, "None", "Senn", "Storm of Flames", 300, T0, "a gnoll", "heat");
        engine.EndCombat();

        var detection = new ClassIdentifier(Fixture).Detect(engine.History[^1].Combatants["SENN"]);
        Assert.Null(detection.ClassName);
        Assert.Equal(1, detection.MappedAbilities);
        Assert.Equal(2, detection.TotalAbilities);
    }

    [Fact]
    public void No_Mapped_Abilities_Means_Unknown()
    {
        var engine = new ParserEngine("log", "Menlu");
        Assert.True(engine.SetEncounter(T0, "Puncher", "a gnoll"));
        engine.AddSwing(SwingCategory.Melee, false, "None", "Puncher", Grammar.EnglishGrammar.AutoAttackAbility, 10, T0, "a gnoll", "crushing");
        engine.EndCombat();

        var detection = new ClassIdentifier(Fixture).Detect(engine.History[^1].Combatants["PUNCHER"]);
        Assert.Null(detection.ClassName);
        Assert.Equal(0, detection.MappedAbilities);
    }

    [Fact]
    public void Embedded_Map_Loads()
    {
        var map = SpellClassMap.LoadEmbedded();
        Assert.True(map.Count > 3000);
        Assert.Contains("Templar", map.ClassesFor("Divine Strike VI"));
        // Effect-cast mining: "Holy Intercession V" logs as its triggered
        // effect "Divine Prayer" — the map must know the effect name.
        Assert.Contains("Templar", map.ClassesFor("Divine Prayer"));
        Assert.Empty(map.ClassesFor("Absolute Vitae"));
    }

    [Fact]
    public void Class_Report_For_A_Real_Fight()
    {
        var path = Environment.GetEnvironmentVariable("EQ2PARSER_SAMPLE_LOG");
        var bossFilter = Environment.GetEnvironmentVariable("EQ2PARSER_BOSS") ?? "Wuoshi";
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            output.WriteLine("EQ2PARSER_SAMPLE_LOG not set — skipped.");
            return;
        }
        var owner = Path.GetFileNameWithoutExtension(path).Replace("eq2log_", "").Split('.')[0];
        var engine = new ParserEngine(path, owner);
        var processor = new LogLineProcessor(engine);
        foreach (var raw in File.ReadLines(path))
            if (LogLine.TryParse(raw, out var line))
                processor.Process(line);
        engine.EndCombat();

        var classifier = new CombatantClassifier(new ClassIdentifier(SpellClassMap.LoadEmbedded()));
        foreach (var encounter in engine.History.Where(e => e.Title.Contains(bossFilter, StringComparison.OrdinalIgnoreCase)))
        {
            output.WriteLine($"[{encounter.Zone}] {encounter.Title} — {encounter.Duration.TotalSeconds:F0}s, success {encounter.GetSuccessLevel()}");
            var tags = classifier.Classify(encounter);
            var byDamage = encounter.Combatants.Values.OrderByDescending(c => c.Damage).ToList();

            foreach (var kind in new[] { CombatantKind.Player, CombatantKind.Pet, CombatantKind.Enemy, CombatantKind.Bystander })
            {
                var group = byDamage.Where(c => tags[c.Key].Kind == kind).ToList();
                output.WriteLine($" {kind} ({group.Count}):");
                foreach (var c in group)
                {
                    var tag = tags[c.Key];
                    var d = tag.Class;
                    var ownerNote = tag.PetOwner is not null ? $" owner={tag.PetOwner}" : "";
                    output.WriteLine($"  {c.Name,-28} {d.ClassName ?? "?",-13} conf {d.Confidence:P0}  ({d.MappedAbilities}/{d.TotalAbilities} abilities)  dmg {c.Damage:N0}{ownerNote}");
                }
            }

            // EQ2PARSER_DUMP_ABILITIES=<name>: list that combatant's distinct
            // abilities with their map lookup — for diagnosing thin verdicts.
            var dump = Environment.GetEnvironmentVariable("EQ2PARSER_DUMP_ABILITIES");
            if (!string.IsNullOrEmpty(dump) && encounter.Combatants.TryGetValue(dump.ToUpperInvariant(), out var target))
            {
                output.WriteLine($" Abilities of {target.Name}:");
                foreach (var ability in ClassIdentifier.CastAbilities(target).OrderBy(a => a, StringComparer.Ordinal))
                {
                    var classes = classifier.Identifier.Map.ClassesFor(ability);
                    output.WriteLine($"  {ability,-40} {(classes.Count == 0 ? "—" : string.Join(", ", classes))}");
                }
            }
        }
    }
}
