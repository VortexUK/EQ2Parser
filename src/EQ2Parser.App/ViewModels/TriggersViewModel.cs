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
public sealed partial class TriggerRow : ObservableObject
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

    public ObservableCollection<TriggerRow> Rows { get; } = [];
    public ObservableCollection<FiredRow> RecentFires { get; } = [];

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

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var trigger in _manager.Triggers.Definitions
                     .OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(t => t.RegexText, StringComparer.OrdinalIgnoreCase))
        {
            if (FilterText.Length > 0
                && !trigger.RegexText.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
                && !trigger.Category.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
                && !trigger.SoundData.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
                continue;
            Rows.Add(new TriggerRow(this, trigger));
        }
        HasTriggers = _manager.Triggers.Definitions.Count > 0;
    }

    internal void SetRowEnabled(TriggerRow row, bool enabled) =>
        _manager.Triggers.SetEnabled(row.Key, enabled);

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
        try
        {
            switch ((TriggerSound)SoundChoice)
            {
                case TriggerSound.Beep:
                    System.Media.SystemSounds.Exclamation.Play();
                    break;
                case TriggerSound.WavFile when text.Length > 0 && System.IO.File.Exists(text):
                    new System.Media.SoundPlayer(text).Play();
                    break;
                case TriggerSound.Tts when text.Length > 0:
                    // Strip $1-style substitutions for the preview.
                    var preview = System.Text.RegularExpressions.Regex.Replace(text, @"\$\{?\w+\}?", "something");
                    using (var tts = new System.Speech.Synthesis.SpeechSynthesizer())
                    {
                        tts.SetOutputToDefaultAudioDevice();
                        tts.Speak(preview);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            EditorError = $"Sound test failed: {ex.Message}";
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
        var triggers = 0;
        var timers = 0;
        var failed = 0;
        foreach (var line in ImportText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (ActShareFormat.TryImport(line))
            {
                case Trigger t:
                    _manager.Triggers.AddOrUpdate(t);
                    triggers++;
                    break;
                case TimerDefinition:
                    timers++;
                    break;
                default:
                    failed++;
                    break;
            }
        }
        List<string> parts = [];
        if (triggers > 0)
            parts.Add($"{triggers} trigger{(triggers == 1 ? "" : "s")} imported");
        if (timers > 0)
            parts.Add($"{timers} spell timer{(timers == 1 ? "" : "s")} skipped (Timers page is next)");
        if (failed > 0)
            parts.Add($"{failed} line{(failed == 1 ? "" : "s")} not recognised");
        ImportResult = parts.Count > 0 ? string.Join(" · ", parts) : "Nothing to import — paste ACT share XML first.";
        if (triggers > 0)
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
