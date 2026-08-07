using System.Windows;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using EQ2Parser.App.ViewModels;
using EQ2Parser.Core.Analysis;

namespace EQ2Parser.App.Views;

/// <summary>Custom clipboard export: presets, sort column, archetype
/// filter, and column checkboxes on the left; live Discord-formatted
/// preview on the right. Copy persists the working configuration so
/// "Copy for Discord" reuses it from then on.</summary>
public partial class ExportWindow : Window
{
    /// <summary>One checkbox row (columns and archetypes share it).</summary>
    public sealed class Choice
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public bool Checked { get; set; }
    }

    /// <summary>One sort-combo entry.</summary>
    public sealed record SortChoice(string Key, string Label);

    private readonly MainParseViewModel _vm;
    private readonly ParseNode _node;
    private readonly List<Choice> _columns;
    private readonly List<Choice> _archetypes;
    private readonly List<SortChoice> _sorts;
    private bool _loading = true;

    public ExportWindow(MainParseViewModel vm, ParseNode node)
    {
        InitializeComponent();
        _vm = vm;
        _node = node;

        var spec = vm.CurrentExportSpec;
        _columns = [.. MainParseViewModel.ExportColumnDefs.Select(d => new Choice
        {
            Key = d.Key,
            Label = d.Label,
            Checked = spec.Columns.Contains(d.Key),
        })];
        ColumnList.ItemsSource = _columns;

        var filtered = spec.Archetypes is { Count: > 0 and < 4 };
        _archetypes = [.. Archetype.All.Select(a => new Choice
        {
            Key = a,
            Label = Loc.Get($"Arch_{a}"),
            Checked = !filtered || spec.Archetypes!.Contains(a),
        })];
        ArchetypeList.ItemsSource = _archetypes;

        _sorts = [new("Damage", Loc.Get("Cols_Damage")), new("Name", Loc.Get("Cols_Name"))];
        _sorts.AddRange(MainParseViewModel.ExportColumnDefs
            .Where(d => d.Key != "Damage")
            .Select(d => new SortChoice(d.Key, d.Label)));
        SortCombo.ItemsSource = _sorts;
        SortCombo.SelectedItem = _sorts.FirstOrDefault(s => s.Key == (spec.SortKey ?? "Damage")) ?? _sorts[0];

        RefreshPresetCombo(selected: null);
        _loading = false;
        RefreshPreview();
    }

    // ── Working spec ────────────────────────────────────────────────────────

    private MainParseViewModel.ExportSpec CurrentSpec()
    {
        var archetypes = _archetypes.Where(a => a.Checked).Select(a => a.Key).ToArray();
        return new MainParseViewModel.ExportSpec(
            [.. _columns.Where(c => c.Checked).Select(c => c.Key)],
            (SortCombo.SelectedItem as SortChoice)?.Key ?? "Damage",
            archetypes.Length is 0 or 4 ? null : archetypes);
    }

    private void RefreshPreview() =>
        PreviewBox.Text = _vm.BuildDiscordExport(_node, CurrentSpec()) ?? "";

    private void Spec_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        StatusText.Text = "";
        RefreshPreview();
    }

    // ── Presets ─────────────────────────────────────────────────────────────

    private void RefreshPresetCombo(string? selected)
    {
        _loading = true;
        PresetCombo.ItemsSource = _vm.ExportPresets.Select(p => p.Name).ToList();
        PresetCombo.SelectedItem = selected;
        DeletePresetButton.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        _loading = false;
    }

    private void Preset_Selected(object sender, RoutedEventArgs e)
    {
        if (_loading || PresetCombo.SelectedItem is not string name)
            return;
        var preset = _vm.ExportPresets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
            return;
        ApplySpec(MainParseViewModel.SpecOf(preset));
        PresetNameBox.Text = preset.Name;
        DeletePresetButton.Visibility = Visibility.Visible;
        StatusText.Text = "";
        RefreshPreview();
    }

    private void ApplySpec(MainParseViewModel.ExportSpec spec)
    {
        _loading = true;
        foreach (var c in _columns)
            c.Checked = spec.Columns.Contains(c.Key);
        ColumnList.ItemsSource = null;
        ColumnList.ItemsSource = _columns;
        var filtered = spec.Archetypes is { Count: > 0 and < 4 };
        foreach (var a in _archetypes)
            a.Checked = !filtered || spec.Archetypes!.Contains(a.Key);
        ArchetypeList.ItemsSource = null;
        ArchetypeList.ItemsSource = _archetypes;
        SortCombo.SelectedItem = _sorts.FirstOrDefault(s => s.Key == (spec.SortKey ?? "Damage")) ?? _sorts[0];
        _loading = false;
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var name = PresetNameBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = Loc.Get("Export_PresetNameTip");
            return;
        }
        _vm.SaveExportPreset(name, CurrentSpec());
        RefreshPresetCombo(selected: name);
        DeletePresetButton.Visibility = Visibility.Visible;
        StatusText.Text = Loc.Format("Export_PresetSaved", name);
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not string name)
            return;
        _vm.DeleteExportPreset(name);
        PresetNameBox.Text = "";
        RefreshPresetCombo(selected: null);
        StatusText.Text = "";
    }

    // ── Copy ────────────────────────────────────────────────────────────────

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
        _vm.SaveExportSpec(CurrentSpec());
        StatusText.Text = Loc.Get("Export_Copied");
    }
}
