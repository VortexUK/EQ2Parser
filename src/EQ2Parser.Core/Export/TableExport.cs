using System.Text;

namespace EQ2Parser.Core.Export;

/// <summary>One export column: header text + alignment (numbers right,
/// names left).</summary>
public sealed record ExportColumn(string Header, bool RightAlign);

/// <summary>
/// Discord-friendly table builder: a summary line, a header row, and
/// aligned data rows inside a fenced code block (Discord renders ``` as
/// monospace, so padding survives). Rows are truncated from the bottom to
/// respect Discord's message limit, with a "+N more" marker.
/// </summary>
public static class TableExport
{
    /// <summary>Discord's message cap is 2000 chars — leave headroom for
    /// the user to add their own words above the paste.</summary>
    public const int DiscordCharLimit = 1900;

    /// <summary>Build the fenced table. <paramref name="moreFormat"/> is the
    /// localized "+{0} more" pattern used when rows are cut for length.</summary>
    public static string BuildDiscord(
        string summary,
        IReadOnlyList<ExportColumn> columns,
        IReadOnlyList<string[]> rows,
        string moreFormat = "+{0} more",
        int charLimit = DiscordCharLimit)
    {
        var kept = rows.Count;
        while (true)
        {
            var text = Render(summary, columns, rows, kept, moreFormat);
            if (text.Length <= charLimit || kept == 0)
                return text;
            kept--;
        }
    }

    private static string Render(
        string summary, IReadOnlyList<ExportColumn> columns,
        IReadOnlyList<string[]> rows, int kept, string moreFormat)
    {
        var widths = new int[columns.Count];
        for (var c = 0; c < columns.Count; c++)
        {
            widths[c] = columns[c].Header.Length;
            for (var r = 0; r < kept; r++)
                widths[c] = Math.Max(widths[c], Cell(rows[r], c).Length);
        }

        var sb = new StringBuilder();
        sb.Append("```\n");
        if (summary.Length > 0)
            sb.Append(summary).Append('\n');
        AppendRow(sb, columns, widths, c => columns[c].Header);
        for (var r = 0; r < kept; r++)
        {
            var row = rows[r];
            AppendRow(sb, columns, widths, c => Cell(row, c));
        }
        if (kept < rows.Count)
            sb.Append(string.Format(moreFormat, rows.Count - kept)).Append('\n');
        sb.Append("```");
        return sb.ToString();
    }

    private static void AppendRow(
        StringBuilder sb, IReadOnlyList<ExportColumn> columns, int[] widths, Func<int, string> cell)
    {
        var line = new StringBuilder();
        for (var c = 0; c < columns.Count; c++)
        {
            if (c > 0)
                line.Append("  ");
            var text = cell(c);
            line.Append(columns[c].RightAlign ? text.PadLeft(widths[c]) : text.PadRight(widths[c]));
        }
        // Trailing pad spaces only inflate the char count — drop them.
        var rendered = line.ToString().TrimEnd();
        sb.Append(rendered).Append('\n');
    }

    private static string Cell(string[] row, int c) => c < row.Length ? row[c] : "";
}
