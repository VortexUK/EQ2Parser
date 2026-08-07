using EQ2Parser.Core.Export;

namespace EQ2Parser.Core.Tests;

public class TableExportTests
{
    private static readonly ExportColumn[] Cols =
    [
        new("NAME", RightAlign: false),
        new("ENCDPS", RightAlign: true),
        new("DMG%", RightAlign: true),
    ];

    [Fact]
    public void Aligns_Columns_Inside_A_Code_Fence()
    {
        var text = TableExport.BuildDiscord(
            "Lord Vyemm · 3:42 · Raid DPS 1.24M",
            Cols,
            [
                ["Sofja", "98.2k", "12.4%"],
                ["Menludiir", "1.2k", "0.2%"],
            ]);

        var lines = text.Split('\n');
        Assert.Equal("```", lines[0]);
        Assert.Equal("Lord Vyemm · 3:42 · Raid DPS 1.24M", lines[1]);
        // Header: NAME padded to the widest name (Menludiir, 9), numbers right-aligned.
        Assert.Equal("NAME       ENCDPS   DMG%", lines[2]);
        Assert.Equal("Sofja       98.2k  12.4%", lines[3]);
        Assert.Equal("Menludiir    1.2k   0.2%", lines[4]);
        Assert.Equal("```", lines[5]);
    }

    [Fact]
    public void Truncates_From_The_Bottom_To_Fit_The_Limit()
    {
        var rows = Enumerable.Range(1, 40)
            .Select(i => new[] { $"Player{i:00}", $"{i}0.0k", "2.5%" })
            .ToList();
        var text = TableExport.BuildDiscord("Big fight", Cols, rows, "+{0} more", charLimit: 400);

        Assert.True(text.Length <= 400, $"length {text.Length}");
        Assert.Contains("Player01", text);
        Assert.DoesNotContain("Player40", text);
        Assert.Matches(@"\+\d+ more", text);
        Assert.EndsWith("```", text);
    }

    [Fact]
    public void Short_Rows_And_Empty_Summary_Are_Safe()
    {
        var text = TableExport.BuildDiscord("", Cols, [["OnlyName"]]);
        var lines = text.Split('\n');
        Assert.Equal("NAME      ENCDPS  DMG%", lines[1]);
        Assert.Equal("OnlyName", lines[2]);
    }

    [Fact]
    public void Zero_Rows_Still_Renders_Header()
    {
        var text = TableExport.BuildDiscord("Empty", Cols, []);
        Assert.Contains("NAME", text);
        Assert.DoesNotContain("more", text);
    }
}
