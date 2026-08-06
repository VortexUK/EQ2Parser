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
        // Conjuror pet kit, same aliasing (incl. the plural-possessive swarm).
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Aery Whip", "Conjuror"));
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("roaring flames' Heat Blast", "Conjuror"));
        // Joust is a crusader CA census only recorded for Paladin — an SK
        // jousting was misread as a Paladin-granted raid proc.
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Joust", "Shadowknight"));
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Joust", "Paladin"));
        // Strike of Faith: multi-class (HO-style) — the effects layer tied
        // it to Inquisitor only, falsely crediting the Inquisitor.
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Strike of Faith", "Berserker"));
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Strike of Faith", null));
        // Charm pets act under the coercer's name and cast the charmed mob's
        // SK kit — class for the SK (own spell) AND the coercer (their play).
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Death Cloud", "Coercer"));
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Painbringer", "Shadowknight"));
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Siphon Strike", "Coercer"));
        // Shadow Step: summoner AA sharing a name with an Assassin scroll —
        // class for necro/conjy; a real Assassin still resolves via the map.
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Shadow Step", "Necromancer"));
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Shadow Step", "Conjuror"));
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Shadow Step", "Assassin"));
        // Unholy Strike + Voracious Soul are REAL SK grants (user-confirmed
        // source spells) — no overrides: the map alone keeps them
        // attributable (Raid on non-SKs, Class on the SK).
        Assert.Equal(AbilitySource.Raid, identifier.ClassifySource("Unholy Strike", "Wizard"));
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Unholy Strike", "Shadowknight"));
        Assert.Equal(AbilitySource.Raid, identifier.ClassifySource("Voracious Soul", "Paladin"));
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Voracious Soul", "Shadowknight"));
    }

    [Fact]
    public void Pet_Kit_On_The_Wrong_Class_Is_The_Renamed_Pet_Signature()
    {
        // Summoners can name their pet after ANOTHER player, merging the
        // pet's damage into that player's parse. The ability-level signature
        // is unfakeable: a class that cannot own the pet showing its kit.
        var identifier = new ClassIdentifier(SpellClassMap.LoadEmbedded(), SourceOverrides.LoadEmbedded());
        Assert.Equal(AbilitySource.Pet, identifier.ClassifySource("Throat Gash", "Brigand"));
        Assert.Equal(AbilitySource.Pet, identifier.ClassifySource("Aery Whip", "Wizard"));
        // Cross-summoner counts too — a necro can't own the conjy pet.
        Assert.Equal(AbilitySource.Pet, identifier.ClassifySource("Aery Whip", "Necromancer"));
        // The shaman dog kit: legit on shamans, renamed-pet on anyone else
        // (seen live: 64 Leg Bleed hits under a Templar).
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Leg Bleed", "Mystic"));
        Assert.Equal(AbilitySource.Class, identifier.ClassifySource("Leg Bleed", "Defiler"));
        Assert.Equal(AbilitySource.Pet, identifier.ClassifySource("Leg Bleed", "Templar"));
        // No detected class → no claim; map fallback (Item) as before.
        Assert.Equal(AbilitySource.Item, identifier.ClassifySource("Throat Gash", null));
        // Swarm composites carry the CASTER's name and can't be renamed
        // onto someone else — not flagged, map fallback applies.
        Assert.Equal(AbilitySource.Item, identifier.ClassifySource("blighted horde's Grave Decay", "Wizard"));
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
