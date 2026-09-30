using EQ2Parser.Core.Upload;

namespace EQ2Parser.Core.Tests;

/// <summary>The raid-only upload rule and its boss-name normaliser (a
/// mirror of the site's _normalise_boss_key — the vectors here are the
/// site's own cases, so a drift on either side shows up as a test).</summary>
public class RaidUploadFilterTests
{
    [Theory]
    [InlineData("Trakanon", "trakanon")]
    [InlineData("  The Herald of Wuoshi  ", "the herald of wuoshi")]
    [InlineData("Vyemm’s Shade", "vyemm's shade")]          // curly apostrophe
    [InlineData("Vyemm`s Shade", "vyemm's shade")]               // grave accent
    [InlineData("Vyemmʼs Shade", "vyemm's shade")]          // modifier letter apostrophe
    [InlineData("Vyemm＇s Shade", "vyemm's shade")]          // fullwidth apostrophe
    [InlineData("Lord Vyemm", "lord vyemm")]                // no-break space
    [InlineData("Lord Vyemm", "lord vyemm")]                // thin space
    [InlineData("trakanon", "trakanon")]                         // idempotent
    public void NormaliseBossName_Matches_The_Site_Key_Shape(string input, string expected) =>
        Assert.Equal(expected, RaidUploadFilter.NormaliseBossName(input));

    [Fact]
    public void NormaliseBossName_Applies_NFC()
    {
        // e + combining acute (NFD) folds to the precomposed é (NFC).
        Assert.Equal("zé", RaidUploadFilter.NormaliseBossName("Zé"));
    }

    [Fact]
    public void Raid_Sized_Fights_Upload_Without_A_Boss_List()
    {
        Assert.True(RaidUploadFilter.IsRaidEncounter("a krait warrior", RaidUploadFilter.RaidMinPlayers, null));
        Assert.True(RaidUploadFilter.IsRaidEncounter("a krait warrior", 24, null));
        Assert.False(RaidUploadFilter.IsRaidEncounter("a krait warrior", RaidMinPlayersMinusOne, null));
        Assert.False(RaidUploadFilter.IsRaidEncounter("Trakanon", 0, null));
    }

    private const int RaidMinPlayersMinusOne = RaidUploadFilter.RaidMinPlayers - 1;

    [Fact]
    public void Curated_Boss_Uploads_At_Any_Headcount()
    {
        var bosses = new HashSet<string>(StringComparer.Ordinal) { "trakanon", "vyemm's shade" };
        Assert.True(RaidUploadFilter.IsRaidEncounter("Trakanon", 1, bosses));
        Assert.True(RaidUploadFilter.IsRaidEncounter("Vyemm’s Shade", 3, bosses)); // curly on the log side
        Assert.False(RaidUploadFilter.IsRaidEncounter("Lord Bob", 6, bosses));
        Assert.False(RaidUploadFilter.IsRaidEncounter("a krait warrior", 6, bosses));
    }

    [Fact]
    public void Group_Size_Is_Six()
    {
        // The site's raid scope starts at 7 — a full group never uploads on headcount.
        Assert.Equal(7, RaidUploadFilter.RaidMinPlayers);
    }
}

public class RaidBossPackTests
{
    [Fact]
    public void Parses_The_Wire_Shape_And_Normalises_Names()
    {
        var pack = RaidBossPack.Parse("""{"version":"abc123def456","bosses":["Trakanon"," vyemm’s shade ",""]}""");
        Assert.NotNull(pack);
        Assert.Equal("abc123def456", pack!.Version);
        var set = pack.ToSet();
        Assert.Equal(2, set.Count);
        Assert.Contains("trakanon", set);
        Assert.Contains("vyemm's shade", set);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"version":"x","bosses":null}""")]
    [InlineData("[]")]
    public void Malformed_Or_Listless_Bodies_Parse_To_Null(string body) =>
        Assert.Null(RaidBossPack.Parse(body));
}
