using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Triggers;
using Trigger = EQ2Parser.Core.Triggers.Trigger;

namespace EQ2Parser.App.ViewModels;

/// <summary>One trigger in the list. Enabled is live — flipping the
/// checkbox pushes straight through to every engine.</summary>
public sealed partial class TriggerRow : ObservableObject, ICategoryDropTarget
{
    private readonly TriggersViewModel _owner;

    public TriggerRow(TriggersViewModel owner, Trigger trigger)
    {
        _owner = owner;
        Trigger = trigger;
        _enabled = trigger.Enabled;
    }

    public Trigger Trigger { get; }
    public string Key => Trigger.Key;
    public string RegexText => Trigger.RegexText;
    public string Category => Trigger.Category;
    public string CategoryName => Trigger.Category;
    public bool ZoneRestricted => Trigger.RestrictToCategoryZone;

    public string SoundLabel => Trigger.SoundType switch
    {
        TriggerSound.Beep => "Beep",
        TriggerSound.WavFile => $"WAV  {System.IO.Path.GetFileName(Trigger.SoundData)}",
        TriggerSound.Tts => $"Say  “{Trigger.SoundData}”",
        _ => "Silent",
    };

    public string TimerLabel => Trigger.StartsTimer && Trigger.TimerName.Length > 0
        ? $"⏱ {Trigger.TimerName}"
        : "";

    [ObservableProperty]
    private bool _enabled;

    /// <summary>Highlight when hovered as a drag target — and reused as the
    /// landing flash after a move.</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    partial void OnEnabledChanged(bool value) => _owner.SetRowEnabled(this, value);
}

/// <summary>One line in the recent-fires feed.</summary>
public sealed record FiredRow(string Time, string Category, string Matched);

/// <summary>
/// The Triggers page: the persisted trigger list (live-edited into every
/// log source's engine), a full editor, ACT XML share-format import/export,
/// and a feed of recent fires so you can watch a trigger work.
/// </summary>
public sealed partial class TriggersViewModel : ObservableObject
{
    private readonly SourceManager _manager;
    private string? _editingKey;

    /// <summary>Flat virtualized tree: CategoryRow headers with
    /// TriggerRow children under the expanded ones (same idiom as the
    /// encounter tree on the Main page).</summary>
    public ObservableCollection<object> Rows { get; } = [];
    public ObservableCollection<FiredRow> RecentFires { get; } = [];

    private readonly HashSet<string> _expandedCategories = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> SoundChoices { get; } = ["Silent", "Beep", "Play WAV file", "Speak text (TTS)"];

    public TriggersViewModel(SourceManager manager)
    {
        _manager = manager;
        RebuildRows();
        manager.Triggers.AlertFired += OnAlertFired;
    }

    // ---- list ----

    [ObservableProperty]
    private string _filterText = "";

    partial void OnFilterTextChanged(string value) => RebuildRows();

    [ObservableProperty]
    private bool _hasTriggers;

    private bool MatchesFilter(Trigger trigger) =>
        FilterText.Length == 0
        || trigger.RegexText.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
        || trigger.Category.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
        || trigger.SoundData.Contains(FilterText, StringComparison.OrdinalIgnoreCase);

    private void RebuildRows()
    {
        Rows.Clear();
        var filtering = FilterText.Length > 0;
        foreach (var group in _manager.Triggers.Definitions
                     .GroupBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            List<Trigger> members = [.. group
                .Where(MatchesFilter)
                .OrderBy(t => t.RegexText, StringComparer.OrdinalIgnoreCase)];
            if (members.Count == 0)
                continue;
            // A filter opens everything it touches; otherwise remembered state.
            var expanded = filtering || _expandedCategories.Contains(group.Key);
            Rows.Add(new CategoryRow(group.Key, expanded)
            {
                Count = members.Count,
                EnabledCount = members.Count(t => t.Enabled),
            });
            if (!expanded)
                continue;
            foreach (var trigger in members)
                Rows.Add(new TriggerRow(this, trigger));
        }
        HasTriggers = _manager.Triggers.Definitions.Count > 0;
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

    /// <summary>Drag-and-drop re-file: rebuild the trigger under its new
    /// category (the category is half the identity key, so this is a keyed
    /// replace that fans out to every engine and persists).</summary>
    public void MoveTrigger(TriggerRow row, string targetCategory)
    {
        targetCategory = targetCategory.Trim();
        if (targetCategory.Length == 0
            || string.Equals(row.Category, targetCategory, StringComparison.OrdinalIgnoreCase))
            return;
        var current = _manager.Triggers.Definitions.FirstOrDefault(t => t.Key == row.Key) ?? row.Trigger;
        var moved = new Trigger(current.RegexText, targetCategory)
        {
            Enabled = current.Enabled,
            RestrictToCategoryZone = current.RestrictToCategoryZone,
            SoundType = current.SoundType,
            SoundData = current.SoundData,
            StartsTimer = current.StartsTimer,
            TimerName = current.TimerName,
            AudioCooldown = current.AudioCooldown,
        };
        _manager.Triggers.AddOrUpdate(moved, replaceKey: current.Key);
        _expandedCategories.Add(targetCategory);
        if (_editingKey == current.Key)
            _editingKey = moved.Key;
        RebuildRows();
        foreach (var item in Rows)
        {
            if (item is TriggerRow landed && landed.Key == moved.Key)
            {
                TriggerMoved?.Invoke(landed);
                break;
            }
        }
    }

    /// <summary>Raised after a drag-move with the row at its new home — the
    /// view scrolls it into view and flashes it.</summary>
    public event Action<TriggerRow>? TriggerMoved;

    internal void SetRowEnabled(TriggerRow row, bool enabled)
    {
        _manager.Triggers.SetEnabled(row.Key, enabled);
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
    private void DeleteRow(TriggerRow? row)
    {
        if (row is null)
            return;
        _manager.Triggers.Remove(row.Key);
        if (_editingKey == row.Key)
            NewTrigger();
        RebuildRows();
    }

    [RelayCommand]
    private void CopyRowXml(TriggerRow? row)
    {
        if (row is null)
            return;
        Clipboard.SetText(ActShareFormat.Export(row.Trigger));
    }

    [RelayCommand]
    private void CopyAllXml()
    {
        var all = _manager.Triggers.Definitions;
        if (all.Count == 0)
            return;
        Clipboard.SetText(string.Join(Environment.NewLine, all.Select(ActShareFormat.Export)));
    }

    // ---- editor ----

    [ObservableProperty]
    private string _editorTitle = "New trigger";

    [ObservableProperty]
    private string _regexText = "";

    [ObservableProperty]
    private string _category = "General";

    [ObservableProperty]
    private bool _restrictToZone;

    [ObservableProperty]
    private int _soundChoice = (int)TriggerSound.Tts;

    [ObservableProperty]
    private string _soundData = "";

    [ObservableProperty]
    private string _cooldownSeconds = "1";

    [ObservableProperty]
    private bool _startsTimer;

    [ObservableProperty]
    private string _timerName = "";

    [ObservableProperty]
    private string _editorError = "";

    public bool SoundDataIsWav => SoundChoice == 2;
    public bool SoundDataIsTts => SoundChoice == 3;
    public bool SoundDataVisible => SoundChoice is 2 or 3;

    partial void OnSoundChoiceChanged(int value)
    {
        OnPropertyChanged(nameof(SoundDataIsWav));
        OnPropertyChanged(nameof(SoundDataIsTts));
        OnPropertyChanged(nameof(SoundDataVisible));
    }

    [RelayCommand]
    private void NewTrigger()
    {
        _editingKey = null;
        EditorTitle = "New trigger";
        RegexText = "";
        Category = "General";
        RestrictToZone = false;
        SoundChoice = (int)TriggerSound.Tts;
        SoundData = "";
        CooldownSeconds = "1";
        StartsTimer = false;
        TimerName = "";
        EditorError = "";
    }

    [RelayCommand]
    private void EditRow(TriggerRow? row)
    {
        if (row is null)
            return;
        var t = row.Trigger;
        _editingKey = t.Key;
        EditorTitle = "Edit trigger";
        RegexText = t.RegexText;
        Category = t.Category;
        RestrictToZone = t.RestrictToCategoryZone;
        SoundChoice = (int)t.SoundType;
        SoundData = t.SoundData;
        CooldownSeconds = t.AudioCooldown.TotalSeconds.ToString("0.#");
        StartsTimer = t.StartsTimer;
        TimerName = t.TimerName;
        EditorError = "";
    }

    [RelayCommand]
    private void SaveTrigger()
    {
        var regex = RegexText.Trim();
        if (regex.Length == 0)
        {
            EditorError = "A regex is required.";
            return;
        }
        var category = Category.Trim();
        if (category.Length == 0)
            category = "General";
        if (!double.TryParse(CooldownSeconds, out var cooldown) || cooldown < 0)
        {
            EditorError = "Cooldown must be a number of seconds.";
            return;
        }
        Trigger trigger;
        try
        {
            trigger = new Trigger(regex, category)
            {
                Enabled = true,
                RestrictToCategoryZone = RestrictToZone,
                SoundType = (TriggerSound)Math.Clamp(SoundChoice, 0, 3),
                SoundData = SoundData.Trim(),
                StartsTimer = StartsTimer && TimerName.Trim().Length > 0,
                TimerName = TimerName.Trim(),
                AudioCooldown = TimeSpan.FromSeconds(Math.Clamp(cooldown, 0, 3600)),
            };
        }
        catch (ArgumentException ex)
        {
            EditorError = $"Invalid regex: {ex.Message}";
            return;
        }
        _manager.Triggers.AddOrUpdate(trigger, _editingKey);
        _editingKey = null;
        _expandedCategories.Add(category);
        NewTrigger();
        RebuildRows();
    }

    [RelayCommand]
    private void BrowseWav()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "WAV files (*.wav)|*.wav|All files (*.*)|*.*",
            Title = "Choose alert sound",
        };
        if (dialog.ShowDialog() == true)
            SoundData = dialog.FileName;
    }

    [RelayCommand]
    private void TestSound()
    {
        var text = SoundData.Trim();
        switch ((TriggerSound)SoundChoice)
        {
            case TriggerSound.Beep:
                _manager.Audio.PlayChime();
                break;
            case TriggerSound.WavFile when text.Length > 0:
                _manager.Audio.PlayFile(text);
                break;
            case TriggerSound.Tts when text.Length > 0:
                // Stand in for $1-style capture substitutions in the preview.
                _manager.Audio.Speak(
                    System.Text.RegularExpressions.Regex.Replace(text, @"\$\{?\w+\}?", "something"));
                break;
        }
    }

    // ---- ACT XML import ----

    [ObservableProperty]
    private string _importText = "";

    [ObservableProperty]
    private string _importResult = "";

    [RelayCommand]
    private void ImportXml()
    {
        List<Trigger> imported = [];
        List<TimerDefinition> timers = [];
        List<int> failedLines = [];
        var lineNo = 0;
        foreach (var line in ImportText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            lineNo++;
            switch (ActShareFormat.TryImport(line))
            {
                case Trigger t:
                    imported.Add(t);
                    break;
                case TimerDefinition d:
                    timers.Add(d);
                    break;
                default:
                    failedLines.Add(lineNo);
                    break;
            }
        }
        _manager.Triggers.AddOrUpdateMany(imported);
        _manager.SpellTimers.ImportMany(timers);
        List<string> parts = [];
        if (imported.Count > 0)
            parts.Add($"{imported.Count} trigger{(imported.Count == 1 ? "" : "s")} imported");
        if (timers.Count > 0)
            parts.Add($"{timers.Count} spell timer{(timers.Count == 1 ? "" : "s")} imported (see Timers page)");
        if (failedLines.Count > 0)
            parts.Add($"{failedLines.Count} line{(failedLines.Count == 1 ? "" : "s")} not recognised (line {string.Join(", ", failedLines.Take(5))}{(failedLines.Count > 5 ? ", …" : "")})");
        ImportResult = parts.Count > 0 ? string.Join(" · ", parts) : "Nothing to import — paste ACT share XML first.";
        if (imported.Count > 0)
        {
            ImportText = "";
            RebuildRows();
        }
    }

    // ---- recent fires ----

    [ObservableProperty]
    private bool _hasRecentFires;

    private void OnAlertFired(TriggerFired fired)
    {
        var row = new FiredRow(
            DateTime.Now.ToString("HH:mm:ss"),
            fired.Trigger.Category,
            fired.Match.Value.Length > 0 ? fired.Match.Value : fired.Trigger.RegexText);
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RecentFires.Insert(0, row);
            while (RecentFires.Count > 30)
                RecentFires.RemoveAt(RecentFires.Count - 1);
            HasRecentFires = true;
        });
    }
}
