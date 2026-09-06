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
        // The raw link is preserved verbatim — it becomes the ledger comment.
        Assert.Equal(@"\aITEM -479148241 279173422:Anaconda Scale Belt\/a", items[0].ItemLink);
        Assert.Equal(@"\aITEM 740343232 1125831452 0 0 0 2 1296083784:Black Unicorn Horn Wristlet\/a", items[4].ItemLink);
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
        Assert.Equal(@"\aITEM -479148241 279173422:Anaconda Scale Belt\/a", item.ItemLink);
        Assert.Equal("Shadynecro", item.LootedBy);
        Assert.Equal("Sawtooth the Ancient", item.Boss);
    }

    [Fact]
    public void Reopening_The_Chest_Does_Not_Duplicate_Contents()
    {
        // Open, close, open again: every open re-logs the remaining
        // contents. The re-observation must top up, not append.
        var t = new LootTracker();
        t.OnLine(Open, T0);
        t.OnLine(ItemBelt, T0);
        t.OnLine(ItemStake, T0);
        t.OnLine(ItemStake, T0); // two real copies
        t.OnLine("Somebody says something", At(2)); // chest closed
        t.OnLine(Open, At(4));
        t.OnLine(ItemBelt, At(4));
        t.OnLine(ItemStake, At(4));
        t.OnLine(ItemStake, At(4));

        Assert.Equal(3, t.Snapshot().Count);
    }

    [Fact]
    public void Reopen_After_Partial_Loot_Lists_Only_The_Remainder()
    {
        var t = new LootTracker();
        t.OnLine(Open, T0);
        t.OnLine(ItemStake, T0);
        t.OnLine(ItemStake, T0);
        t.OnLine(@"Ariadneh loots \aITEM 24961957 -2088560038:Sharpened Heartwood Stake\/a from the Exquisite Chest of Treah Greenroot.", At(10));
        // Re-open: the looted copy is gone from the chest, one remains.
        t.OnLine(Open, At(20));
        t.OnLine(ItemStake, At(20));

        var items = t.Snapshot();
        Assert.Equal(2, items.Count); // still the two real drops
        Assert.Equal(1, items.Count(i => i.LootedBy is not null));
    }

    [Fact]
    public void Reopen_Showing_More_Copies_Adds_Only_The_Excess()
    {
        // A second chest of the same type (or a missed line) can raise the
        // observed count — only the excess is new.
        var t = new LootTracker();
        t.OnLine(Open, T0);
        t.OnLine(ItemStake, T0);
        t.OnLine("Somebody says something", At(2));
        t.OnLine(Open, At(4));
        t.OnLine(ItemStake, At(4));
        t.OnLine(ItemStake, At(4));

        Assert.Equal(2, t.Snapshot().Count);
    }

    [Fact]
    public void Same_Item_From_A_Different_Chest_Type_Is_A_New_Drop()
    {
        var t = new LootTracker();
        t.OnLine(Open, T0);
        t.OnLine(ItemBelt, T0);
        t.OnLine("Somebody says something", At(2));
        t.OnLine("Ariadneh opens Ornate Chest and discovers: ", At(4));
        t.OnLine(ItemBelt, At(4));

        Assert.Equal(2, t.Snapshot().Count);
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
    public void Loot_Command_Deducts_Via_Mains_With_The_Link_As_Comment()
    {
        var mains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Alty"] = "Mainy" };
        var charge = new LootCharge("Alty", 25, "Torc of Winding Waters", @"\aITEM -809902670 226691356:Torc of Winding Waters\/a");
        // The deduction targets the MAIN; the announcement names the raid
        // character who actually received the item.
        Assert.Equal(
            @"guild points add -25 Mainy \aITEM -809902670 226691356:Torc of Winding Waters\/a",
            DkpCommandFile.LootCommand(charge, mains));
        Assert.Equal(
            @"\aITEM -809902670 226691356:Torc of Winding Waters\/a assigned to Alty for 25 dkp",
            DkpCommandFile.LootAnnouncement(charge));
    }

    [Fact]
    public void Loot_Command_Without_Link_Or_Mains_Uses_The_Sanitised_Name()
    {
        var charge = new LootCharge("Buyer", 10, "Sharpened  Heartwood\r\nStake");
        Assert.Equal("guild points add -10 Buyer Sharpened Heartwood Stake", DkpCommandFile.LootCommand(charge));
        Assert.Equal("Sharpened Heartwood Stake assigned to Buyer for 10 dkp", DkpCommandFile.LootAnnouncement(charge));
    }

    [Fact]
    public void Overlong_Link_Falls_Back_To_The_Plain_Name()
    {
        // SanitizeReason caps comments at 120 chars; a truncated link would
        // leave broken \aITEM markup in the guild event, so the name wins.
        var hugeLink = @"\aITEM 740343232 1125831452 0 0 0 2 1296083784:" + new string('X', 120) + @"\/a";
        var charge = new LootCharge("Buyer", 15, "Readable Name", hugeLink);
        Assert.Equal("guild points add -15 Buyer Readable Name", DkpCommandFile.LootCommand(charge));
    }

    [Fact]
    public void Loot_Step_File_Is_Command_Announcement_Marker()
    {
        var charge = new LootCharge("Alty", 25, "Torc", @"\aITEM 1 2:Torc\/a");
        var text = DkpCommandFile.BuildStepFile(
            DkpCommandFile.LootCommand(charge), DkpCommandFile.LootAnnouncement(charge), DkpCommandFile.LootMarkerCommand);
        Assert.Equal(
            "guild points add -25 Alty \\aITEM 1 2:Torc\\/a\r\n"
            + "of \\aITEM 1 2:Torc\\/a assigned to Alty for 25 dkp\r\n"
            + "eq2lexicon_loot_done\r\n",
            text);
    }
}
