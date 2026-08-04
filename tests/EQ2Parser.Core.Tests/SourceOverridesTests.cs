using EQ2Parser.Core.Analysis;

namespace EQ2Parser.Core.Tests;

public class SourceOverridesTests
{
    private static readonly SpellClassMap EmptyMap = SpellClassMap.FromDictionary([]);

    [Fact]
    public void Embedded_Overrides_Fix_The_Known_Mislabels()
    {
        var identifier = new ClassIdentifier(SpellClassMap.LoadEmbedded(), SourceOverrides.LoadEmbedded());
        // Templar hammer pet's ability logs under the owner and is absent
        // from census, so the map said Item. Inquisitors don't get the
        // hammer — for them the map fallback (Item) is the right answer.
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Divine Smash", "Templar"));
        Assert.Equal(AbilitySource.Item, identifier.ClassifySource("Divine Smash", "Inquisitor"));
        Assert.Equal(AbilitySource.Item, identifier.ClassifySource("Divine Smash", "Wizard"));
        Assert.Equal(AbilitySource.Item, identifier.ClassifySource("Divine Smash", null));
        // Tunare's Wrath bow proc — shares a name with a Fury/Warden spell,
        // so non-druids were mislabelled Raid.
        Assert.Equal(AbilitySource.Item, identifier.ClassifySource("Tunare's Grace", "Templar"));
        Assert.Equal(AbilitySource.Item, identifier.ClassifySource("Tunare's Grace", "Fury"));
        // Heroic Opportunity finisher + Freeblood racial proc — the
        // character's own play, not items (absent from census → Item before).
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Ringing Blow", "Wizard"));
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Bloodletting", null));
        // Necro scout-pet kit logs under the owner's name (verified: every
        // attacker in the logs is a necromancer); swarm-pet attacks parse as
        // a composite "pet's Ability" name via the double possessive.
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Throat Gash", "Necromancer"));
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("blighted horde's Grave Decay", "Necromancer"));
        // Non-necros fall back to the map (absent from both layers → Item).
        Assert.Equal(AbilitySource.Item, identifier.ClassifySource("Throat Gash", "Brigand"));
        // Conjuror pet kit, same aliasing (incl. the plural-possessive swarm).
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Aery Whip", "Conjuror"));
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("roaring flames' Heat Blast", "Conjuror"));
        Assert.Equal(AbilitySource.Item, identifier.ClassifySource("Aery Whip", "Wizard"));
    }

    [Fact]
    public void Unscoped_Rule_Applies_To_Any_Class()
    {
        var overrides = SourceOverrides.FromJson(
            """{ "overrides": [ { "ability": "Mystery Proc", "source": "raid" } ] }""");
        var identifier = new ClassIdentifier(EmptyMap, overrides);
        Assert.Equal(AbilitySource.Raid, identifier.ClassifySource("Mystery Proc", "Wizard"));
        Assert.Equal(AbilitySource.Raid, identifier.ClassifySource("Mystery Proc IV", null)); // roman-stripped
    }

    [Fact]
    public void Overrides_Outrank_The_Map_And_System_List()
    {
        var map = SpellClassMap.FromDictionary(new() { ["known spell"] = ["Dirge"] });
        var overrides = SourceOverrides.FromJson(
            """{ "overrides": [ { "ability": "Known Spell", "classes": ["Dirge"], "source": "item" } ] }""");
        var identifier = new ClassIdentifier(map, overrides);
        // The map would say Class (dirge casting a dirge spell); the override wins.
        Assert.Equal(AbilitySource.Item, identifier.ClassifySource("Known Spell", "Dirge"));
    }

    [Fact]
    public void Local_File_Rules_Win_Over_Earlier_Rules()
    {
        var overrides = SourceOverrides.FromJson(
            """{ "overrides": [ { "ability": "Contested", "source": "item" } ] }""");
        var dir = Directory.CreateTempSubdirectory("eq2parser-overrides-").FullName;
        try
        {
            var path = Path.Combine(dir, "source_overrides.json");
            File.WriteAllText(path, """{ "overrides": [ { "ability": "Contested", "source": "class" } ] }""");
            overrides.MergeFile(path);
            Assert.Null(overrides.LoadError);
            Assert.True(overrides.TryResolve("Contested", "Templar", out var source));
            Assert.Equal(AbilitySource.Class, source);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Malformed_Local_File_Is_Skipped_With_A_Reason()
    {
        var overrides = SourceOverrides.FromJson("""{ "overrides": [] }""");
        var dir = Directory.CreateTempSubdirectory("eq2parser-overrides-").FullName;
        try
        {
            var path = Path.Combine(dir, "source_overrides.json");
            File.WriteAllText(path, "{ not json at all");
            overrides.MergeFile(path);
            Assert.NotNull(overrides.LoadError);
            Assert.Contains("source_overrides.json", overrides.LoadError);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_Local_File_Is_A_Clean_NoOp()
    {
        var overrides = SourceOverrides.LoadEmbedded().MergeFile(@"Z:\does\not\exist.json");
        Assert.Null(overrides.LoadError);
        Assert.True(overrides.TryResolve("Divine Smash", "Templar", out _));
    }

    [Fact]
    public void Bad_Entries_Are_Ignored_Not_Fatal()
    {
        var overrides = SourceOverrides.FromJson(
            """
            { "overrides": [
                { "ability": "", "source": "class" },
                { "ability": "No Source" },
                { "ability": "Bad Source", "source": "wibble" },
                { "ability": "Good One", "source": "system" }
            ] }
            """);
        Assert.False(overrides.TryResolve("No Source", null, out _));
        Assert.False(overrides.TryResolve("Bad Source", null, out _));
        Assert.True(overrides.TryResolve("Good One", null, out var source));
        Assert.Equal(AbilitySource.System, source);
    }
}
