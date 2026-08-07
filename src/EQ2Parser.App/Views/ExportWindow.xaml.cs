using System.Windows;
using EQ2Parser.App.Localization;
using EQ2Parser.App.ViewModels;

namespace EQ2Parser.App.Views;

/// <summary>Custom clipboard export: pick columns on the left, live
/// Discord-formatted preview on the right. Copy persists the selection
/// so "Copy for Discord" reuses it from then on.</summary>
public partial class ExportWindow : Window
{
    /// <summary>One checkbox row.</summary>
    public sealed class ColumnChoice
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public bool Checked { get; set; }
    }

    private readonly MainParseViewModel _vm;
    private readonly ParseNode _node;
    private readonly List<ColumnChoice> _choices;
    private bool _loading = true;

    public ExportWindow(MainParseViewModel vm, ParseNode node)
    {
        InitializeComponent();
        _vm = vm;
        _node = node;
        var selected = new HashSet<string>(vm.ExportColumnKeys, StringComparer.Ordinal);
        _choices = [.. MainParseViewModel.ExportColumnDefs.Select(d => new ColumnChoice
        {
            Key = d.Key,
            Label = d.Label,
            Checked = selected.Contains(d.Key),
        })];
        ColumnList.ItemsSource = _choices;
        _loading = false;
        RefreshPreview();
    }

    private IReadOnlyList<string> SelectedKeys() =>
        [.. _choices.Where(c => c.Checked).Select(c => c.Key)];

    private void RefreshPreview() =>
        PreviewBox.Text = _vm.BuildDiscordExport(_node, SelectedKeys()) ?? "";

    private void Column_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        StatusText.Text = "";
        RefreshPreview();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = PreviewBox.Text;
        if (text.Length == 0)
            return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
            // Clipboard briefly owned elsewhere — the preview still shows,
            // the user can select + Ctrl-C manually.
        }
        _vm.SaveExportColumns(SelectedKeys());
        StatusText.Text = Loc.Get("Export_Copied");
    }
}
