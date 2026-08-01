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
    public string Zone => Trigger.Zone;
    public bool ZoneRestricted => Trigger.RestrictToCategoryZone;

    public string SoundLabel => Trigger.SoundType switch
    {
        TriggerSound.Beep => "Beep",
        TriggerSound.WavFile => $"WAV  {System.IO.Path.GetFileName(Trigger.SoundData)}",
        TriggerSound.Tts => $"Say  “{Trigger.SoundData}”",
        _ => "Silent",
    };

    /// <summary>The linked spell timer is gone (deleted after the link was
    /// made) — the trigger would fire but no bar would start.</summary>
    public bool LinkedTimerMissing =>
        Trigger.StartsTimer && Trigger.TimerName.Length > 0 && !_owner.HasTimerNamed(Trigger.TimerName);

    public string TimerLabel => Trigger.StartsTimer && Trigger.TimerName.Length > 0
        ? $"⏱ {Trigger.TimerName}{(LinkedTimerMissing ? " — no such timer!" : "")}"
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

    /// <summary>Flat virtualized tree: ZoneRow → CategoryRow (mob) →
    /// TriggerRow, same three-level idiom as the Timers page.</summary>
    public ObservableCollection<object> Rows { get; } = [];
    public ObservableCollection<FiredRow> RecentFires { get; } = [];

    private readonly HashSet<string> _collapsedZones = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedCategories = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> SoundChoices { get; } = ["Silent", "Beep", "Play WAV file", "Speak text (TTS)"];

    public TriggersViewModel(SourceManager manager)
    {
        _manager = manager;
        RebuildRows();
        manager.Triggers.AlertFired += OnAlertFired;
        manager.Triggers.DefinitionsChanged += () =>
            Application.Current?.Dispatcher.BeginInvoke(RebuildRows);
        // Timer edits too: the rows show linked-timer state ("no such
        // timer!"), which changes when a timer is created or deleted.
        manager.SpellTimers.DefinitionsChanged += () =>
            Application.Current?.Dispatcher.BeginInvoke(RebuildRows);
    }

    internal bool HasTimerNamed(string name) =>
        _manager.SpellTimers.Definitions.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The trigger-side half of a linked pair is useless without
    /// the timer-side half — ACT quietly auto-registers unknown spell
    /// timers, and so do we: a 30s default the user then tunes.</summary>
    private int EnsureLinkedTimers(IEnumerable<Trigger> triggers)
    {
        var created = 0;
        foreach (var trigger in triggers)
        {
            if (!trigger.StartsTimer || trigger.TimerName.Length == 0 || HasTimerNamed(trigger.TimerName))
                continue;
            _manager.SpellTimers.AddOrUpdate(new TimerDefinition
            {
                Name = trigger.TimerName,
                Category = trigger.Category,
                Zone = trigger.Zone,
                // Trigger-started: the notify's attacker is whatever the
                // regex matched, so a category lock would strangle it.
                RestrictToCategory = false,
                WarningSoundData = "tts",
            });
            created++;
        }
        return created;
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
        || trigger.Zone.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
        || trigger.SoundData.Contains(FilterText, StringComparison.OrdinalIgnoreCase);

    private static string ZoneKey(Trigger trigger) =>
        trigger.Zone.Length > 0 ? trigger.Zone : "General";

    private void RebuildRows()
    {
        Rows.Clear();
        var filtering = FilterText.Length > 0;
        foreach (var zoneGroup in _manager.Triggers.Definitions
                     .GroupBy(ZoneKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            List<Trigger> zoneMembers = [.. zoneGroup.Where(MatchesFilter)];
            if (zoneMembers.Count == 0)
                continue;
            // Zones default OPEN (there are few); categories default closed.
            var zoneExpanded = filtering || !_collapsedZones.Contains(zoneGroup.Key);
            Rows.Add(new ZoneRow(zoneGroup.Key, zoneExpanded)
            {
                Count = zoneMembers.Count,
                EnabledCount = zoneMembers.Count(t => t.Enabled),
            });
            if (!zoneExpanded)
                continue;
            foreach (var group in zoneMembers
                         .GroupBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                List<Trigger> members = [.. group
                    .OrderBy(t => t.RegexText, StringComparer.OrdinalIgnoreCase)];
                var expandKey = $"{zoneGroup.Key}|{group.Key}";
                // A filter opens everything it touches; otherwise remembered state.
                var expanded = filtering || _expandedCategories.Contains(expandKey);
                Rows.Add(new CategoryRow(group.Key, expanded)
                {
                    Tag = zoneGroup.Key,
                    Count = members.Count,
                    EnabledCount = members.Count(t => t.Enabled),
                });
                if (!expanded)
                    continue;
                foreach (var trigger in members)
                    Rows.Add(new TriggerRow(this, trigger));
            }
        }
        HasTriggers = _manager.Triggers.Definitions.Count > 0;
    }

    [RelayCommand]
    private void ToggleZone(ZoneRow? row)
    {
        if (row is null)
            return;
        if (!_collapsedZones.Add(row.Name))
            _collapsedZones.Remove(row.Name);
        RebuildRows();
    }

    [RelayCommand]
    private void ToggleCategory(CategoryRow? row)
    {
        if (row is null)
            return;
        var key = $"{row.Tag}|{row.Name}";
        if (!_expandedCategories.Add(key))
            _expandedCategories.Remove(key);
        RebuildRows();
    }

    /// <summary>Drag-and-drop re-file onto a mob header or one of its rows:
    /// the trigger adopts the target's category AND zone (both are identity),
    /// so it lands exactly where it was dropped.</summary>
    public void MoveTrigger(TriggerRow row, ICategoryDropTarget target)
    {
        var targetCategory = target.CategoryName.Trim();
        var targetZone = target switch
        {
            CategoryRow header => header.Tag is "General" or null ? "" : header.Tag,
            TriggerRow other => other.Zone,
            _ => row.Zone,
        };
        if (targetCategory.Length == 0)
            return;
        var current = _manager.Triggers.Definitions.FirstOrDefault(t => t.Key == row.Key) ?? row.Trigger;
        if (string.Equals(current.Category, targetCategory, StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.Zone, targetZone, StringComparison.OrdinalIgnoreCase))
            return;
        var moved = new Trigger(current.RegexText, targetCategory, targetZone)
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
        _collapsedZones.Remove(targetZone.Length > 0 ? targetZone : "General");
        _expandedCategories.Add($"{(targetZone.Length > 0 ? targetZone : "General")}|{targetCategory}");
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
        var zoneKey = ZoneKey(row.Trigger);
        foreach (var item in Rows)
        {
            if (item is CategoryRow header
                && header.Name.Equals(row.Category, StringComparison.OrdinalIgnoreCase)
                && string.Equals(header.Tag, zoneKey, StringComparison.OrdinalIgnoreCase))
                header.EnabledCount += enabled ? 1 : -1;
            else if (item is ZoneRow zone
                && zone.Name.Equals(zoneKey, StringComparison.OrdinalIgnoreCase))
                zone.EnabledCount += enabled ? 1 : -1;
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
    private string _zoneText = "";

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

    [ObservableProperty]
    private string _editorInfo = "";

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
        ZoneText = "";
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
        ZoneText = t.Zone;
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
            trigger = new Trigger(regex, category, ZoneText.Trim())
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
        EditorInfo = EnsureLinkedTimers([trigger]) > 0
            ? $"Also created spell timer “{trigger.TimerName}” (30s default) — set its real length on the Timers page."
            : "";
        var zoneKey = trigger.Zone.Length > 0 ? trigger.Zone : "General";
        _collapsedZones.Remove(zoneKey);
        _expandedCategories.Add($"{zoneKey}|{category}");
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
        // AFTER the explicit <Spell> lines land, so a pasted pair links up
        // rather than spawning a default next to the real definition.
        var linked = EnsureLinkedTimers(imported);
        List<string> parts = [];
        if (imported.Count > 0)
            parts.Add($"{imported.Count} trigger{(imported.Count == 1 ? "" : "s")} imported");
        if (timers.Count > 0)
            parts.Add($"{timers.Count} spell timer{(timers.Count == 1 ? "" : "s")} imported (see Timers page)");
        if (linked > 0)
            parts.Add($"{linked} linked timer{(linked == 1 ? "" : "s")} created with 30s defaults — set real lengths on the Timers page");
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
