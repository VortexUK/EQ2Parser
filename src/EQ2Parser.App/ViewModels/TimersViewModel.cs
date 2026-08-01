using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Triggers;
using Trigger = EQ2Parser.Core.Triggers.Trigger;

namespace EQ2Parser.App.ViewModels;

/// <summary>One timer definition in the list. Enabled is live — the
/// checkbox pushes straight through to the shared timer service.</summary>
public sealed partial class TimerDefRow : ObservableObject, ICategoryDropTarget
{
    private readonly TimersViewModel _owner;

    public TimerDefRow(TimersViewModel owner, TimerDefinition definition)
    {
        _owner = owner;
        Definition = definition;
        _enabled = definition.Enabled;
    }

    public TimerDefinition Definition { get; }
    public string Name => Definition.Name;
    public string Category => Definition.Category;
    public string CategoryName => Definition.Category;
    public string Key => Definition.Key;

    [ObservableProperty]
    private bool _enabled;

    /// <summary>Drop-target hover highlight; reused as the landing flash.</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    partial void OnEnabledChanged(bool value) => _owner.SetRowEnabled(this, value);

    public string DetailLabel
    {
        get
        {
            List<string> parts = [$"{Definition.DurationSeconds}s"];
            if (Definition.WarningSeconds > 0)
                parts.Add($"warn {Definition.WarningSeconds}s");
            if (Definition.RestrictToMe)
                parts.Add("only mine");
            if (Definition.AbsoluteTiming)
                parts.Add("one at a time");
            if (Definition.RestrictToCategory)
                parts.Add("category-locked");
            if (Definition.OnlyMasterTicks)
                parts.Add("restart only");
            if (!Definition.Modable)
                parts.Add("mods off");
            if (Definition.StartSoundData.Length > 0 || Definition.WarningSoundData.Length > 0)
                parts.Add("🔊");
            return string.Join("  ·  ", parts);
        }
    }

    public Brush ColorBrush
    {
        get
        {
            var argb = unchecked((uint)Definition.FillColorArgb);
            return new SolidColorBrush(Color.FromArgb(0xFF, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        }
    }
}

/// <summary>One live bar in the preview panel.</summary>
public sealed partial class TimerBarRow : ObservableObject
{
    [ObservableProperty]
    private string _label = "";

    [ObservableProperty]
    private string _secondsText = "";

    [ObservableProperty]
    private double _fraction;

    [ObservableProperty]
    private Brush _barBrush = Brushes.SteelBlue;

    [ObservableProperty]
    private bool _isWarning;
}

/// <summary>
/// The Timers page: spell-timer definitions (ACT &lt;Spell&gt; XML import,
/// editor, per-timer test), a live preview of running bars, and the
/// in-game overlay controls.
/// </summary>
public sealed partial class TimersViewModel : ObservableObject
{
    private readonly SourceManager _manager;
    private string? _editingKey;

    /// <summary>Flat virtualized tree: CategoryRow headers with TimerDefRow
    /// children under the expanded ones — same idiom as Triggers.</summary>
    public ObservableCollection<object> Rows { get; } = [];
    public ObservableCollection<TimerBarRow> LiveBars { get; } = [];

    private readonly HashSet<string> _expandedCategories = new(StringComparer.OrdinalIgnoreCase);

    public TimersViewModel(SourceManager manager)
    {
        _manager = manager;
        RebuildRows();
        // Definitions change from other windows too (Curation, future
        // Lexicon sync) — stay current.
        manager.SpellTimers.DefinitionsChanged += () =>
            Application.Current?.Dispatcher.BeginInvoke(RebuildRows);
    }

    // ---- definition list ----

    [ObservableProperty]
    private string _filterText = "";

    partial void OnFilterTextChanged(string value) => RebuildRows();

    [ObservableProperty]
    private bool _hasTimers;

    private bool MatchesFilter(TimerDefinition def) =>
        FilterText.Length == 0
        || def.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
        || def.Category.Contains(FilterText, StringComparison.OrdinalIgnoreCase);

    private void RebuildRows()
    {
        Rows.Clear();
        var filtering = FilterText.Length > 0;
        foreach (var group in _manager.SpellTimers.Definitions
                     .GroupBy(d => d.Category, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            List<TimerDefinition> members = [.. group
                .Where(MatchesFilter)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];
            if (members.Count == 0)
                continue;
            // A filter opens everything it touches; otherwise remembered state.
            var expanded = filtering || _expandedCategories.Contains(group.Key);
            Rows.Add(new CategoryRow(group.Key, expanded)
            {
                Count = members.Count,
                EnabledCount = members.Count(d => d.Enabled),
            });
            if (!expanded)
                continue;
            foreach (var def in members)
                Rows.Add(new TimerDefRow(this, def));
        }
        HasTimers = _manager.SpellTimers.Definitions.Count > 0;
    }

    [RelayCommand]
    private void ToggleCategory(CategoryRow? row)
    {
        if (row is null)
            return;
        if (!_expandedCategories.Add(row.Name))
            _expandedCategories.Remove(row.Name);
        RebuildRows();
    }

    /// <summary>Drag-and-drop re-file: the category is half the identity
    /// key, so this is a keyed replace that persists immediately.</summary>
    public void MoveTimer(TimerDefRow row, string targetCategory)
    {
        targetCategory = targetCategory.Trim();
        if (targetCategory.Length == 0
            || string.Equals(row.Category, targetCategory, StringComparison.OrdinalIgnoreCase))
            return;
        var current = _manager.SpellTimers.Definitions.FirstOrDefault(d => d.Key == row.Key) ?? row.Definition;
        var moved = current with { Category = targetCategory };
        _manager.SpellTimers.AddOrUpdate(moved, replaceKey: current.Key);
        _expandedCategories.Add(targetCategory);
        if (_editingKey == current.Key)
            _editingKey = moved.Key;
        RebuildRows();
        foreach (var item in Rows)
        {
            if (item is TimerDefRow landed && landed.Key == moved.Key)
            {
                TimerMoved?.Invoke(landed);
                break;
            }
        }
    }

    /// <summary>Raised after a drag-move with the row at its new home — the
    /// view scrolls it into view and flashes it.</summary>
    public event Action<TimerDefRow>? TimerMoved;

    internal void SetRowEnabled(TimerDefRow row, bool enabled)
    {
        _manager.SpellTimers.SetEnabled(row.Key, enabled);
        foreach (var item in Rows)
        {
            if (item is CategoryRow header
                && header.Name.Equals(row.Category, StringComparison.OrdinalIgnoreCase))
            {
                header.EnabledCount += enabled ? 1 : -1;
                break;
            }
        }
    }

    [RelayCommand]
    private void DeleteRow(TimerDefRow? row)
    {
        if (row is null)
            return;
        _manager.SpellTimers.Remove(row.Key);
        if (_editingKey == row.Key)
            NewTimer();
        RebuildRows();
    }

    [RelayCommand]
    private void CopyRowXml(TimerDefRow? row)
    {
        if (row is null)
            return;
        Clipboard.SetText(ActShareFormat.Export(row.Definition));
    }

    [RelayCommand]
    private void CopyAllXml()
    {
        var all = _manager.SpellTimers.Definitions;
        if (all.Count == 0)
            return;
        Clipboard.SetText(string.Join(Environment.NewLine, all.Select(ActShareFormat.Export)));
    }

    [RelayCommand]
    private void TestRow(TimerDefRow? row)
    {
        if (row is null)
            return;
        _manager.SpellTimers.StartTest(row.Definition);
    }

    // ---- editor ----

    public IReadOnlyList<string> ColorChoices { get; } =
        ["Blue", "Red", "Green", "Gold", "Purple", "Cyan", "Orange", "White"];

    private static readonly int[] ColorValues =
    [
        unchecked((int)0xFF3B82F6), unchecked((int)0xFFF87171), unchecked((int)0xFF4ADE80),
        unchecked((int)0xFFC8A96E), unchecked((int)0xFFA78BFA), unchecked((int)0xFF22D3EE),
        unchecked((int)0xFFFB923C), unchecked((int)0xFFE2E4F0),
    ];

    [ObservableProperty]
    private string _editorTitle = "New timer";

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _category = "General";

    [ObservableProperty]
    private string _durationSeconds = "30";

    [ObservableProperty]
    private string _warningSeconds = "10";

    [ObservableProperty]
    private string _removeSeconds = "-15";

    [ObservableProperty]
    private int _colorChoice;

    [ObservableProperty]
    private bool _restrictToMe;

    [ObservableProperty]
    private bool _absoluteTiming;

    [ObservableProperty]
    private bool _restrictToCategory;

    [ObservableProperty]
    private bool _onlyMasterTicks;

    [ObservableProperty]
    private bool _modable = true;

    [ObservableProperty]
    private bool _radialDisplay;

    [ObservableProperty]
    private bool _panel1 = true;

    [ObservableProperty]
    private bool _panel2;

    [ObservableProperty]
    private string _tooltip = "";

    [ObservableProperty]
    private string _startSound = "";

    [ObservableProperty]
    private string _warningSound = "";

    [ObservableProperty]
    private string _editorError = "";

    private int _customColor;
    private bool _hasCustomColor;

    [RelayCommand]
    private void NewTimer()
    {
        _editingKey = null;
        _hasCustomColor = false;
        EditorTitle = "New timer";
        Name = "";
        Category = "General";
        DurationSeconds = "30";
        WarningSeconds = "10";
        RemoveSeconds = "-15";
        ColorChoice = 0;
        RestrictToMe = false;
        AbsoluteTiming = false;
        RestrictToCategory = false;
        OnlyMasterTicks = false;
        Modable = true;
        RadialDisplay = false;
        Panel1 = true;
        Panel2 = false;
        Tooltip = "";
        StartSound = "";
        WarningSound = "";
        EditorError = "";
    }

    [RelayCommand]
    private void EditRow(TimerDefRow? row)
    {
        if (row is null)
            return;
        var d = row.Definition;
        _editingKey = d.Key;
        EditorTitle = "Edit timer";
        Name = d.Name;
        Category = d.Category;
        DurationSeconds = d.DurationSeconds.ToString();
        WarningSeconds = d.WarningSeconds.ToString();
        RemoveSeconds = d.RemoveSeconds.ToString();
        var index = Array.IndexOf(ColorValues, d.FillColorArgb);
        _hasCustomColor = index < 0;
        _customColor = d.FillColorArgb;
        ColorChoice = index >= 0 ? index : 0;
        RestrictToMe = d.RestrictToMe;
        AbsoluteTiming = d.AbsoluteTiming;
        RestrictToCategory = d.RestrictToCategory;
        OnlyMasterTicks = d.OnlyMasterTicks;
        Modable = d.Modable;
        RadialDisplay = d.RadialDisplay;
        Panel1 = d.Panel1;
        Panel2 = d.Panel2;
        Tooltip = d.Tooltip;
        StartSound = d.StartSoundData;
        WarningSound = d.WarningSoundData;
        EditorError = "";
    }

    [RelayCommand]
    private void SaveTimer()
    {
        var name = Name.Trim();
        if (name.Length == 0)
        {
            EditorError = "The timer needs a name — it matches the ability/spell name in the log.";
            return;
        }
        if (!int.TryParse(DurationSeconds, out var duration) || duration < 1 || duration > 24 * 3600)
        {
            EditorError = "Duration must be a whole number of seconds.";
            return;
        }
        if (!int.TryParse(WarningSeconds, out var warning) || warning < 0)
        {
            EditorError = "Warning must be a whole number of seconds (0 = no warning).";
            return;
        }
        if (!int.TryParse(RemoveSeconds, out var remove))
        {
            EditorError = "Remove-at must be a whole number of seconds (negative = linger past zero).";
            return;
        }
        var category = Category.Trim();
        var definition = new TimerDefinition
        {
            Name = name,
            Category = category.Length == 0 ? "General" : category,
            DurationSeconds = duration,
            WarningSeconds = Math.Min(warning, duration),
            RemoveSeconds = remove,
            FillColorArgb = _hasCustomColor && ColorChoice == 0 ? _customColor : ColorValues[Math.Clamp(ColorChoice, 0, ColorValues.Length - 1)],
            RestrictToMe = RestrictToMe,
            AbsoluteTiming = AbsoluteTiming,
            RestrictToCategory = RestrictToCategory,
            OnlyMasterTicks = OnlyMasterTicks,
            Modable = Modable,
            RadialDisplay = RadialDisplay,
            Panel1 = Panel1,
            Panel2 = Panel2,
            Tooltip = Tooltip.Trim(),
            StartSoundData = StartSound.Trim(),
            WarningSoundData = WarningSound.Trim(),
        };
        _manager.SpellTimers.AddOrUpdate(definition, _editingKey);
        _editingKey = null;
        _expandedCategories.Add(definition.Category);
        NewTimer();
        RebuildRows();
    }

    // ---- ACT XML import (accepts <Spell> and <Trigger> lines alike) ----

    [ObservableProperty]
    private string _importText = "";

    [ObservableProperty]
    private string _importResult = "";

    [RelayCommand]
    private void ImportXml()
    {
        List<TimerDefinition> timers = [];
        List<Trigger> triggers = [];
        var failed = 0;
        foreach (var line in ImportText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (ActShareFormat.TryImport(line))
            {
                case TimerDefinition d:
                    timers.Add(d);
                    break;
                case Trigger t:
                    triggers.Add(t);
                    break;
                default:
                    failed++;
                    break;
            }
        }
        _manager.SpellTimers.ImportMany(timers);
        _manager.Triggers.AddOrUpdateMany(triggers);
        List<string> parts = [];
        if (timers.Count > 0)
            parts.Add($"{timers.Count} timer{(timers.Count == 1 ? "" : "s")} imported");
        if (triggers.Count > 0)
            parts.Add($"{triggers.Count} trigger{(triggers.Count == 1 ? "" : "s")} imported (see Triggers page)");
        if (failed > 0)
            parts.Add($"{failed} line{(failed == 1 ? "" : "s")} not recognised");
        ImportResult = parts.Count > 0 ? string.Join(" · ", parts) : "Nothing to import — paste ACT share XML first.";
        if (timers.Count > 0 || triggers.Count > 0)
        {
            ImportText = "";
            RebuildRows();
        }
    }

    // ---- live preview (driven by the shell tick while this page is visible) ----

    public void Refresh()
    {
        var bars = _manager.SpellTimers.Snapshot(DateTimeOffset.Now);
        while (LiveBars.Count > bars.Count)
            LiveBars.RemoveAt(LiveBars.Count - 1);
        while (LiveBars.Count < bars.Count)
            LiveBars.Add(new TimerBarRow());
        for (var i = 0; i < bars.Count; i++)
            ApplyBar(LiveBars[i], bars[i]);
    }

    /// <summary>Shared bar shaping for the preview and the overlay.</summary>
    public static void ApplyBar(TimerBarRow row, TimerBarSnapshot bar)
    {
        var combatant = bar.Combatant;
        if (combatant.Length > 0 && char.IsLower(combatant[0]))
            combatant = char.ToUpperInvariant(combatant[0]) + combatant[1..];
        row.Label = combatant.Length > 0 && !combatant.Equals("you", StringComparison.OrdinalIgnoreCase)
            ? $"{bar.Name} · {combatant}"
            : bar.Name;
        row.SecondsText = bar.SecondsLeft >= 0
            ? bar.SecondsLeft >= 60
                ? $"{(int)bar.SecondsLeft / 60}:{(int)bar.SecondsLeft % 60:00}"
                : $"{bar.SecondsLeft:0.0}"
            : $"{bar.SecondsLeft:0}";
        row.Fraction = Math.Clamp(bar.SecondsLeft / Math.Max(1, bar.DurationSeconds), 0, 1);
        row.IsWarning = bar.SecondsLeft <= bar.WarningSeconds;
        var argb = unchecked((uint)bar.FillColorArgb);
        var brush = new SolidColorBrush(Color.FromArgb(0xFF, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        row.BarBrush = brush;
    }
}
