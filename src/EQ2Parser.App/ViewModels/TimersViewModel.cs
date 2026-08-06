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
    public string CategoryScope => Definition.Source.Length > 0
        ? $"lex|{(Definition.Zone.Length > 0 ? Definition.Zone : "General")}"
        : Definition.Zone.Length > 0 ? Definition.Zone : "General";
    public string Key => Definition.Key;

    /// <summary>Synced from EQ2Lexicon — read-only (no edit/delete/drag);
    /// the enable checkbox persists as a local override.</summary>
    public bool IsLexicon => Definition.Source.Length > 0;

    [ObservableProperty]
    private bool _enabled;

    /// <summary>Drop-target hover highlight; reused as the landing flash.</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    /// <summary>This row is loaded in the editor — highlighted so what's
    /// being edited is unmistakable.</summary>
    [ObservableProperty]
    private bool _isEditing;

    partial void OnEnabledChanged(bool value) => _owner.SetRowEnabled(this, value);

    public string DetailLabel
    {
        get
        {
            List<string> parts = [$"{Definition.DurationSeconds}s"];
            if (Definition.WarningSeconds > 0)
                parts.Add($"warn {Definition.WarningSeconds}s");
            if (Definition.ControlEffect is { Length: > 0 } cc)
                parts.Add(cc.ToUpperInvariant());
            if (Definition.DamageType is { Length: > 0 } dt)
                parts.Add(dt);
            if (Definition.RestrictToMe)
                parts.Add("only mine");
            if (Definition.AbsoluteTiming)
                parts.Add("one at a time");
            if (Definition.RestrictToCategory)
                parts.Add("category-locked");
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

/// <summary>One damage-school toggle chip in the timer editor.</summary>
public sealed partial class SchoolChip(string name, Action<SchoolChip> toggled) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isSelected;

    private bool _syncing;

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_syncing)
            toggled(this);
    }

    /// <summary>Programmatic sync (editor open) — no toggle callback.</summary>
    public void Set(bool selected)
    {
        _syncing = true;
        IsSelected = selected;
        _syncing = false;
    }
}

/// <summary>One live bar in the preview panel / overlay.</summary>
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

    // ---- flame styling (overlay bars) ----

    /// <summary>Rebuild guard: brushes only regenerate when the colour or
    /// tags change, not on every 100ms tick.</summary>
    internal string StyleKey = "";

    /// <summary>Fill gradient — dark base to school-coloured flame front.</summary>
    [ObservableProperty]
    private Brush _fillBrush = Brushes.SteelBlue;

    /// <summary>Glow colour behind the bar: the dominant damage school,
    /// or the bar's own colour when untagged.</summary>
    [ObservableProperty]
    private Color _glowColor = Colors.SteelBlue;

    /// <summary>Readable (lightened) school colour for the info chip.</summary>
    [ObservableProperty]
    private Brush _schoolBrush = Brushes.Gainsboro;

    [ObservableProperty]
    private string _damageText = "";

    [ObservableProperty]
    private string _controlText = "";

    /// <summary>Name without the combatant suffix — radial cells are too
    /// small for "Prone to Corruption · Othysis Muravian".</summary>
    [ObservableProperty]
    private string _nameOnly = "";

    /// <summary>"⇑50%" when recast debuffs (Traumatic Swipe) stretched this
    /// timer past its base duration; "" when unmodified.</summary>
    [ObservableProperty]
    private string _swipeText = "";
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
    public ObservableCollection<object> Rows => _tree.Rows;
    public ObservableCollection<TimerBarRow> LiveBars { get; } = [];

    private readonly CategoryTree<TimerDefinition> _tree;

    public TimersViewModel(SourceManager manager)
    {
        _manager = manager;
        _tree = new CategoryTree<TimerDefinition>
        {
            ZoneOf = static d => d.Zone,
            CategoryOf = static d => d.Category,
            IsLexicon = static d => d.Source.Length > 0,
            EnabledOf = static d => d.Enabled,
            SortKeyOf = static d => d.Name,
            MakeRow = d => new TimerDefRow(this, d),
            LexiconHost = () => manager.Lexicon.BaseUrl.Replace("https://", ""),
        };
        foreach (var school in Vocabulary.DamageSchools)
            DamageSchoolChips.Add(new SchoolChip(school, OnChipToggled));
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
        || def.Category.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
        || def.Zone.Contains(FilterText, StringComparison.OrdinalIgnoreCase);

    private void RebuildRows()
    {
        var all = _manager.SpellTimers.Definitions;
        _tree.Rebuild(all, MatchesFilter, FilterText.Length > 0);
        HasTimers = all.Count > 0;
        MarkEditingRow();
    }

    /// <summary>Sync the editing highlight to _editingKey — rows are
    /// recreated on every rebuild, so the flag must be re-applied.</summary>
    private void MarkEditingRow()
    {
        foreach (var item in Rows)
        {
            if (item is TimerDefRow row)
                row.IsEditing = _editingKey is not null && row.Key == _editingKey;
        }
    }

    [RelayCommand]
    private void ToggleZone(ZoneRow? row)
    {
        if (row is null)
            return;
        _tree.ToggleZone(row);
        RebuildRows();
    }

    [RelayCommand]
    private void ToggleCategory(CategoryRow? row)
    {
        if (row is null)
            return;
        _tree.ToggleCategory(row);
        RebuildRows();
    }

    /// <summary>Drag-and-drop re-file onto a mob header or one of its rows:
    /// the timer adopts the target's mob (category, half the identity key)
    /// AND its zone, so it lands exactly where it was dropped.</summary>
    public void MoveTimer(TimerDefRow row, ICategoryDropTarget target)
    {
        // Lexicon rows are curator-owned: never a drag source, never a
        // landing zone (fork-to-Custom is the way to adopt one).
        if (row.IsLexicon
            || target is TimerDefRow { IsLexicon: true }
            || (target is CategoryRow c && c.Tag?.StartsWith("lex|", StringComparison.Ordinal) == true))
            return;
        var targetCategory = target.CategoryName.Trim();
        var targetZone = target switch
        {
            CategoryRow header => header.Tag is "General" or null ? "" : header.Tag,
            TimerDefRow other => other.Definition.Zone,
            _ => row.Definition.Zone,
        };
        if (targetCategory.Length == 0)
            return;
        var current = _manager.SpellTimers.Definitions.FirstOrDefault(d => d.Key == row.Key) ?? row.Definition;
        if (string.Equals(current.Category, targetCategory, StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.Zone, targetZone, StringComparison.OrdinalIgnoreCase))
            return;
        var moved = current with { Category = targetCategory, Zone = targetZone };
        _manager.SpellTimers.AddOrUpdate(moved, replaceKey: current.Key);
        _tree.Reveal(targetZone, targetCategory);
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
        _tree.PatchEnabledCounts(row.Category, _tree.TagFor(row.Definition), enabled);
    }

    [RelayCommand]
    private void DeleteRow(TimerDefRow? row)
    {
        if (row is null || row.IsLexicon)
            return;
        var definition = row.Definition;
        _manager.SpellTimers.Remove(row.Key);
        _manager.Undo.Push(() => _manager.SpellTimers.AddOrUpdate(definition));
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
    private string _zoneText = "";

    // string?, not string: clearing a ComboBox's bound items pushes null
    // back through the TwoWay SelectedItem binding into these.
    [ObservableProperty]
    private string? _damageTypeText = "";

    [ObservableProperty]
    private string? _controlEffectText = "";

    /// <summary>Control-effect dropdown choices — the Core closed
    /// vocabulary plus blank, plus the row's current value when off-list.</summary>
    public ObservableCollection<string> ControlEffectChoices { get; } = [];

    /// <summary>Damage type is MULTI-select: one toggle chip per school.
    /// Toggle order is dominance order — the first-picked school leads the
    /// stored combo and drives the flame colour.</summary>
    public ObservableCollection<SchoolChip> DamageSchoolChips { get; } = [];

    private readonly List<string> _damageOrder = [];

    private void OnChipToggled(SchoolChip chip)
    {
        if (chip.IsSelected)
        {
            if (!_damageOrder.Contains(chip.Name))
                _damageOrder.Add(chip.Name);
        }
        else
        {
            _damageOrder.Remove(chip.Name);
        }
        DamageTypeText = string.Join(", ", _damageOrder);
    }

    /// <summary>Rebuild the choice list FIRST, then assign the value:
    /// clearing a list the ComboBox is showing nulls the bound text via
    /// the selection binding, so values assigned before the rebuild get
    /// silently wiped (the second-edit crash).</summary>
    private void SetTagFields(string damageType, string controlEffect)
    {
        _damageOrder.Clear();
        foreach (var token in damageType.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var canonical = Vocabulary.DamageSchools.FirstOrDefault(s => s.Equals(token, StringComparison.OrdinalIgnoreCase));
            if (canonical is not null && !_damageOrder.Contains(canonical))
                _damageOrder.Add(canonical);
        }
        foreach (var chip in DamageSchoolChips)
            chip.Set(_damageOrder.Contains(chip.Name));
        DamageTypeText = string.Join(", ", _damageOrder);
        RebuildChoices(ControlEffectChoices, Vocabulary.ControlEffects, controlEffect);
        ControlEffectText = controlEffect;
    }

    private static void RebuildChoices(ObservableCollection<string> target, IReadOnlyList<string> canonical, string current)
    {
        target.Clear();
        target.Add("");
        foreach (var choice in canonical)
            target.Add(choice);
        if (current.Length > 0 && !target.Any(c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase)))
            target.Add(current);
    }

    [ObservableProperty]
    private string _durationSeconds = "30";

    [ObservableProperty]
    private string _warningSeconds = "10";

    [ObservableProperty]
    private string _removeSeconds = "-15";

    [ObservableProperty]
    private int _colorChoice;

    /// <summary>Any user palette selection drops the off-palette colour an
    /// edit loaded — without this, "Blue" (slot 0) was unselectable on a
    /// timer that came in with a custom colour.</summary>
    partial void OnColorChoiceChanged(int value) => _hasCustomColor = false;

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
        ZoneText = "";
        SetTagFields("", "");
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
        MarkEditingRow();
    }

    [RelayCommand]
    private void EditRow(TimerDefRow? row)
    {
        if (row is null || row.IsLexicon)
            return;
        var d = row.Definition;
        _editingKey = d.Key;
        EditorTitle = "Edit timer";
        Name = d.Name;
        Category = d.Category;
        ZoneText = d.Zone;
        SetTagFields(d.DamageType, d.ControlEffect);
        DurationSeconds = d.DurationSeconds.ToString();
        WarningSeconds = d.WarningSeconds.ToString();
        RemoveSeconds = d.RemoveSeconds.ToString();
        var index = Array.IndexOf(ColorValues, d.FillColorArgb);
        _customColor = d.FillColorArgb;
        // Set ColorChoice FIRST — its change handler clears the custom
        // flag (a user selection always means "use the palette entry").
        ColorChoice = index >= 0 ? index : 0;
        _hasCustomColor = index < 0;
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
        MarkEditingRow();
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
            Zone = ZoneText.Trim(),
            DamageType = (DamageTypeText ?? "").Trim(),
            ControlEffect = (ControlEffectText ?? "").Trim(),
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
        // Stay ON the saved timer — the editor emptying itself after every
        // save read as the work vanishing. "New timer" is the way out.
        _editingKey = definition.Key;
        EditorTitle = "Edit timer";
        _tree.Reveal(definition.Zone, definition.Category);
        EditorError = "";
        RebuildRows();
    }

    // ---- ACT XML import (accepts <Spell> and <Trigger> lines alike) ----

    [ObservableProperty]
    private string _importText = "";

    [ObservableProperty]
    private string _importResult = "";

    /// <summary>Import a FULL ACT settings export file (Config with
    /// CustomTriggers + SpellTimers — hundreds of entries).</summary>
    [RelayCommand]
    private void ImportXmlFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import ACT settings XML",
            Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;
        string xml;
        try
        {
            xml = System.IO.File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ImportResult = $"Couldn't read the file: {ex.Message}";
            return;
        }
        var result = ActConfigImport.TryImport(xml);
        if (result is null)
        {
            ImportResult = "Not a recognisable ACT XML export (no Trigger/Spell entries found).";
            return;
        }
        _manager.SpellTimers.ImportMany(result.Timers);
        _manager.Triggers.AddOrUpdateMany(result.Triggers);
        var linked = _manager.SpellTimers.EnsureLinkedTimers(result.Triggers);
        List<string> parts = [];
        if (result.Timers.Count > 0)
            parts.Add($"{result.Timers.Count} spell timer{(result.Timers.Count == 1 ? "" : "s")} imported");
        if (result.Triggers.Count > 0)
            parts.Add($"{result.Triggers.Count} trigger{(result.Triggers.Count == 1 ? "" : "s")} imported (see Triggers page)");
        if (linked > 0)
            parts.Add($"{linked} linked timer{(linked == 1 ? "" : "s")} created with 30s defaults");
        if (result.Skipped > 0)
            parts.Add($"{result.Skipped} entr{(result.Skipped == 1 ? "y" : "ies")} skipped (bad regex or missing name)");
        ImportResult = parts.Count > 0 ? string.Join(" · ", parts) : "The file contained no importable entries.";
        RebuildRows();
    }

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
        // Same pass the Triggers page runs (this one had drifted and
        // skipped it): pasted <Trigger> lines that start unknown timers
        // get 30s defaults, AFTER explicit <Spell> lines land.
        var linked = _manager.SpellTimers.EnsureLinkedTimers(triggers);
        List<string> parts = [];
        if (timers.Count > 0)
            parts.Add($"{timers.Count} timer{(timers.Count == 1 ? "" : "s")} imported");
        if (triggers.Count > 0)
            parts.Add($"{triggers.Count} trigger{(triggers.Count == 1 ? "" : "s")} imported (see Triggers page)");
        if (linked > 0)
            parts.Add($"{linked} linked timer{(linked == 1 ? "" : "s")} created with 30s defaults");
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
        // Above the style short-circuit: the key doesn't include the name,
        // so a recycled radial row kept showing the previous timer's name.
        row.NameOnly = bar.Name;
        // Also per-tick (a swipe can land mid-timer on a recycled row).
        row.SwipeText = bar.SwipedPercent > 0 ? $"⇑{bar.SwipedPercent}%" : "";
        var styleKey = $"{bar.FillColorArgb}|{bar.DamageType}|{bar.ControlEffect}";
        if (row.StyleKey == styleKey)
            return;
        row.StyleKey = styleKey;
        var argb = unchecked((uint)bar.FillColorArgb);
        var fill = Color.FromArgb(0xFF, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        // ACT's untouched default (pure #0000FF) is nobody's choice — show
        // it as the app gold instead. Any deliberately picked colour,
        // including a real blue that isn't exactly the default, is kept.
        if (bar.FillColorArgb == unchecked((int)0xFF0000FF))
            fill = Color.FromRgb(0xC8, 0xA9, 0x6E);
        var brush = new SolidColorBrush(fill);
        brush.Freeze();
        row.BarBrush = brush;
        var glow = SchoolPalette.For(bar.DamageType, fill);
        row.GlowColor = glow;
        row.FillBrush = SchoolPalette.FlameFill(fill, glow);
        var schoolBrush = new SolidColorBrush(SchoolPalette.Blend(glow, Colors.White, 0.4));
        schoolBrush.Freeze();
        row.SchoolBrush = schoolBrush;
        row.DamageText = bar.DamageType;
        row.ControlText = bar.ControlEffect.Length > 0 ? bar.ControlEffect.ToUpperInvariant() : "";
    }
}
