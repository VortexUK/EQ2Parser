using EQ2Parser.Core.Raid;

namespace EQ2Parser.Core.Tests;

/// <summary>Raid loot: chest-content blocks + loots assignment + the DKP
/// purchase deductions. Line shapes are verbatim from the 2026-07-25 raid
/// night corpus.</summary>
public sealed class LootTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 25, 20, 2, 25, TimeSpan.Zero);

    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    private const string Open = "Ariadneh opens Exquisite Chest and discovers: ";
    private const string ItemBelt = @"     \aITEM -479148241 279173422:Anaconda Scale Belt\/a";
    private const string ItemTorc = @"     \aITEM -809902670 226691356:Torc of Winding Waters\/a";
    private const string ItemStake = @"     \aITEM 24961957 -2088560038:Sharpened Heartwood Stake\/a";
    private const string ItemLongLink = @"     \aITEM 740343232 1125831452 0 0 0 2 1296083784:Black Unicorn Horn Wristlet\/a";

    [Fact]
    public void Chest_Block_Lists_Contents_Including_Duplicates()
    {
        var t = new LootTracker();
        t.OnLine(Open, T0);
        t.OnLine(ItemBelt, T0);
        t.OnLine(ItemTorc, T0);
        t.OnLine(ItemStake, T0);
        t.OnLine(ItemStake, T0); // two of the same item really drop
        t.OnLine(ItemLongLink, T0); // 7-number ITEM link form

        var items = t.Snapshot();
        Assert.Equal(5, items.Count);
        Assert.Equal(
            ["Anaconda Scale Belt", "Torc of Winding Waters", "Sharpened Heartwood Stake", "Sharpened Heartwood Stake", "Black Unicorn Horn Wristlet"],
            items.Select(i => i.ItemName).ToList());
        Assert.All(items, i => Assert.Null(i.LootedBy));
        Assert.All(items, i => Assert.Equal("Exquisite Chest", i.ChestType));
    }

    [Fact]
    public void Loots_Line_Assigns_First_Unassigned_Duplicate_And_Backfills_Boss()
    {
        var t = new LootTracker();
        t.OnLine(Open, T0);
        t.OnLine(ItemStake, T0);
        t.OnLine(ItemStake, T0);
        t.OnLine(@"Ariadneh loots \aITEM 24961957 -2088560038:Sharpened Heartwood Stake\/a from the Exquisite Chest of Treah Greenroot.", At(60));
        t.OnLine(@"Catofur loots \aITEM 24961957 -2088560038:Sharpened Heartwood Stake\/a from the Exquisite Chest of Treah Greenroot.", At(61));

        var items = t.Snapshot();
        Assert.Equal(2, items.Count);
        Assert.Equal(["Ariadneh", "Catofur"], items.Select(i => i.LootedBy).ToList());
        Assert.All(items, i => Assert.Equal("Treah Greenroot", i.Boss));
    }

    [Fact]
    public void Loots_Without_Prior_Open_Creates_The_Item()
    {
        var t = new LootTracker();
        t.OnLine(@"Shadynecro loots \aITEM -479148241 279173422:Anaconda Scale Belt\/a from the Exquisite Chest of Sawtooth the Ancient.", T0);
        var item = Assert.Single(t.Snapshot());
        Assert.Equal("Anaconda Scale Belt", item.ItemName);
        Assert.Equal("Shadynecro", item.LootedBy);
        Assert.Equal("Sawtooth the Ancient", item.Boss);
    }

    [Fact]
    public void Corpse_Loot_Is_Ignored()
    {
        var t = new LootTracker();
        t.OnLine(@"Ariadneh loots \aITEM 1577251774 352911834:reptile meat\/a from the corpse of Sawtooth the Ancient.", T0);
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Chest_Block_Closes_On_Other_Lines_And_Timeout()
    {
        var t = new LootTracker();
        t.OnLine(Open, T0);
        t.OnLine("Somebody says something in raid chat", T0);
        t.OnLine(ItemBelt, T0); // block closed — this line is ignored
        Assert.Empty(t.Snapshot());

        t.OnLine(Open, At(30));
        t.OnLine(ItemBelt, At(30 + 10)); // past BlockTimeout — ignored
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void New_Session_Clears()
    {
        var t = new LootTracker();
        t.OnLine(Open, T0);
        t.OnLine(ItemBelt, T0);
        Assert.Single(t.Snapshot());
        t.StartNewSession();
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Prefilter_Accepts_Loot_Shapes()
    {
        Assert.True(LootTracker.LooksRelevant(Open));
        Assert.True(LootTracker.LooksRelevant(ItemBelt));
        Assert.True(LootTracker.LooksRelevant(@"X loots \aITEM 1 2:Y\/a from the Exquisite Chest of Z."));
        Assert.False(LootTracker.LooksRelevant("Guildmate: Coyi has logged in."));
        // The raid prefilter (the RaidLine gate) includes the loot shapes.
        Assert.True(RaidRosterTracker.LooksRelevant(Open));
    }

    [Fact]
    public void Loot_Commands_Deduct_Via_Mains()
    {
        var mains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Alty"] = "Mainy", ["Mainy"] = "Mainy" };
        var charges = new List<LootCharge>
        {
            new("Alty", 25, "Torc of Winding Waters"),
            new("Mainy", 10, "Sharpened  Heartwood\r\nStake"), // sanitised comment
            new("a mob", 5, "Junk"), // non-player buyer dropped
            new("Mainy", 0, "Freebie"), // zero cost dropped
        };
        var lines = DkpCommandFile.BuildLootCommands(charges, mains);
        Assert.Equal("guild points add -25 Mainy Torc of Winding Waters", lines[0]); // alt pays via main
        Assert.Equal("guild points add -10 Mainy Sharpened Heartwood Stake", lines[1]);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void Loot_Commands_Without_Mains_Charge_The_Buyer_Directly()
    {
        // Unlike awards there is no bulk fallback — each charge always
        // targets its buyer, and duplicates are never deduped (buying two
        // items costs twice).
        var charges = new List<LootCharge>
        {
            new("Buyer", 15, "Kudzu Coil Whip"),
            new("Buyer", 15, "Kudzu Coil Whip"),
        };
        var lines = DkpCommandFile.BuildLootCommands(charges, null);
        Assert.Equal("guild points add -15 Buyer Kudzu Coil Whip", lines[0]);
        Assert.Equal("guild points add -15 Buyer Kudzu Coil Whip", lines[1]);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void Loot_Queue_File_Carries_The_Loot_Marker()
    {
        var text = DkpCommandFile.BuildQueueFile(["guild points add -10 Buyer Thing"], DkpCommandFile.LootMarkerCommand);
        Assert.Equal("guild points add -10 Buyer Thing\r\neq2lexicon_loot_done\r\n", text);
        // Empty queue = marker-only file, same safety as the award file.
        Assert.Equal("eq2lexicon_loot_done\r\n", DkpCommandFile.BuildQueueFile([], DkpCommandFile.LootMarkerCommand));
    }
}
