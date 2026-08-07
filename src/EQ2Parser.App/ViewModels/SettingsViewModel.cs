using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.ViewModels;

/// <summary>One downloadable voice pack in the Settings list.</summary>
public sealed partial class VoicePackRow(NeuralPack pack) : ObservableObject
{
    public NeuralPack Pack { get; } = pack;
    public string Name => Pack.DisplayName;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _actionLabel = Loc.Get("SettingsVm_ActionDownload");

    [ObservableProperty]
    private bool _busy;
}

/// <summary>A selectable UI language (code + native display name).</summary>
public sealed record LanguageChoice(string Code, string DisplayName);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SourceManager _manager;

    [ObservableProperty]
    private string _idleEndSeconds;

    [ObservableProperty]
    private string _pollMilliseconds;

    [ObservableProperty]
    private string _historyBossDays;

    [ObservableProperty]
    private string _historyTrashDays;

    [ObservableProperty]
    private string _status = "";

    // ---- alert audio (applied to the live service immediately; Save persists) ----

    public IReadOnlyList<TtsVoice> Voices { get; }

    [ObservableProperty]
    private TtsVoice? _selectedVoice;

    [ObservableProperty]
    private double _ttsRate;

    [ObservableProperty]
    private double _alertVolume;

    [ObservableProperty]
    private string _voiceStatus = "";

    public IReadOnlyList<VoicePackRow> PackRows { get; }

    private string? _downloadingArchive;

    /// <summary>Slider-friendly inverse of the stored depth factor:
    /// 0 = natural, 0.3 = deepest (factor 0.7 ≈ six semitones down).</summary>
    [ObservableProperty]
    private double _voiceDepthAmount;

    // ---- UI language (persisted immediately; applies on restart) ----

    public IReadOnlyList<LanguageChoice> LanguageChoices { get; } =
        [.. Loc.Languages.Select(l => new LanguageChoice(l.Code, l.DisplayName))];

    [ObservableProperty]
    private LanguageChoice? _selectedLanguage;

    [ObservableProperty]
    private string _languageStatus = "";

    private bool _loadingLanguage;

    partial void OnSelectedLanguageChanged(LanguageChoice? value)
    {
        if (_loadingLanguage || value is null)
            return;
        _manager.Settings = _manager.Settings with { LanguageCode = value.Code };
        _manager.Settings.Save();
        LanguageStatus = Loc.Get("SettingsVm_LanguageRestartHint");
    }

    public string TtsRateLabel => $"{TtsRate:0.0}×";
    public string VoiceDepthLabel => VoiceDepthAmount < 0.005 ? Loc.Get("SettingsVm_DepthOff") : $"{VoiceDepthAmount:P0}";
    public string AlertVolumeLabel => $"{AlertVolume:P0}";

    partial void OnTtsRateChanged(double value)
    {
        _manager.Audio.SpeakingRate = value;
        OnPropertyChanged(nameof(TtsRateLabel));
        PersistAudio();
    }

    partial void OnVoiceDepthAmountChanged(double value)
    {
        _manager.Audio.VoiceDepth = 1 - value;
        OnPropertyChanged(nameof(VoiceDepthLabel));
        PersistAudio();
    }

    partial void OnAlertVolumeChanged(double value)
    {
        _manager.Audio.Volume = value;
        OnPropertyChanged(nameof(AlertVolumeLabel));
        PersistAudio();
    }

    /// <summary>Audio prefs apply immediately, so they persist immediately
    /// too — picking a voice and never touching Save used to lose it on
    /// restart. The Save button remains for the validated parsing fields.
    /// Sliders fire per drag-delta, so the disk write is debounced.</summary>
    private void PersistAudio()
    {
        _manager.Settings = _manager.Settings with
        {
            TtsVoiceId = SelectedVoice?.Id,
            TtsRate = TtsRate,
            TtsDepth = 1 - VoiceDepthAmount,
            AlertVolume = AlertVolume,
        };
        AppSettings.SaveSoon(() => _manager.Settings);
    }

    partial void OnSelectedVoiceChanged(TtsVoice? value)
    {
        _manager.Audio.VoiceId = value?.Id;
        PersistAudio();
        if (value is null || PiperVoiceCatalog.Find(value.Id) is not { } neural)
        {
            VoiceStatus = "";
            return;
        }
        if (PiperVoiceCatalog.IsInstalled(neural))
        {
            VoiceStatus = Loc.Get("SettingsVm_NeuralReady");
            return;
        }
        if (PiperVoiceCatalog.PackFor(neural) is { } pack)
        {
            VoiceStatus = Loc.Format("SettingsVm_NeedsPack", pack.SizeMb);
            _ = DownloadPackAsync(FindRow(pack));
        }
    }

    private VoicePackRow FindRow(NeuralPack pack) => PackRows.First(r => r.Pack.Archive == pack.Archive);

    [RelayCommand]
    private async Task PackAction(VoicePackRow? row)
    {
        if (row is null || row.Busy)
            return;
        if (!PiperVoiceCatalog.IsPackInstalled(row.Pack))
        {
            await DownloadPackAsync(row);
            return;
        }
        // Remove: unload the engine first so the model file isn't mapped.
        // Off-thread — Unload waits on the synth gate, and a Synthesize
        // mid-model-load holds it for seconds (this froze the UI).
        row.Busy = true;
        try
        {
            await Task.Run(() =>
            {
                _manager.Audio.UnloadNeuralModel();
                PiperVoiceCatalog.RemovePack(row.Pack);
            });
            RefreshPackRows();
            if (PiperVoiceCatalog.Find(SelectedVoice?.Id) is { } current && current.Archive == row.Pack.Archive)
                VoiceStatus = Loc.Get("SettingsVm_PackRemoved");
        }
        catch (Exception ex)
        {
            row.Status = Loc.Format("SettingsVm_RemoveFailed", ex.Message);
        }
        finally
        {
            row.Busy = false;
        }
    }

    private async Task DownloadPackAsync(VoicePackRow row)
    {
        if (_downloadingArchive is not null)
        {
            row.Status = Loc.Get("SettingsVm_DownloadBusy");
            return;
        }
        _downloadingArchive = row.Pack.Archive;
        row.Busy = true;
        try
        {
            var progress = new Progress<double>(p => row.Status = Loc.Format("SettingsVm_Downloading", p, row.Pack.SizeMb));
            await PiperVoiceCatalog.DownloadAsync(row.Pack.PackVoices[0], progress, CancellationToken.None);
            RefreshPackRows();
            if (PiperVoiceCatalog.Find(SelectedVoice?.Id) is { } current && current.Archive == row.Pack.Archive)
                VoiceStatus = Loc.Get("SettingsVm_Installed");
        }
        catch (Exception ex)
        {
            row.Status = Loc.Format("SettingsVm_DownloadFailed", ex.Message);
            row.ActionLabel = Loc.Get("SettingsVm_ActionDownload");
        }
        finally
        {
            _downloadingArchive = null;
            row.Busy = false;
        }
    }

    private void RefreshPackRows()
    {
        foreach (var row in PackRows)
        {
            if (PiperVoiceCatalog.IsPackInstalled(row.Pack))
            {
                var mb = PiperVoiceCatalog.InstalledBytes(row.Pack) / (1024.0 * 1024.0);
                row.Status = Loc.Format("SettingsVm_InstalledSize", mb);
                row.ActionLabel = Loc.Get("SettingsVm_ActionRemove");
            }
            else
            {
                row.Status = Loc.Format("SettingsVm_PackSize", row.Pack.SizeMb);
                row.ActionLabel = Loc.Get("SettingsVm_ActionDownload");
            }
        }
    }

    public string VersionLabel => $"EQ2Parser v{_manager.Updates.CurrentVersion}";

    [ObservableProperty]
    private string _updateStatus = "";

    public SettingsViewModel(SourceManager manager)
    {
        _manager = manager;
        _updateStatus = manager.Updates.Status;
        manager.Updates.StatusChanged += status =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => UpdateStatus = status);
        _lexiconStatus = manager.Lexicon.Status;
        manager.Lexicon.StatusChanged += () =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => LexiconStatus = manager.Lexicon.Status);
        // A broken neural voice must say SO — the silent Windows-voice
        // fallback cost a tester a session of confusion.
        manager.Audio.NeuralVoiceFailed += reason =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                VoiceStatus = Loc.Format("SettingsVm_NeuralFailed", reason));
        _loadingUpload = true;
        UploadEnabled = manager.Settings.UploadEnabled;
        _loadingUpload = false;
        _uploadTokenState = Loc.Get(manager.Settings.LexiconApiTokenProtected is null ? "SettingsVm_NoTokenSaved" : "SettingsVm_TokenSavedState");
        _uploadStatus = manager.Uploads.Status;
        manager.Uploads.StatusChanged += () =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => UploadStatus = manager.Uploads.Status);
        _loadingCallouts = true;
        CalloutsEnabled = manager.Settings.CalloutsEnabled;
        CalloutMinPlayers = manager.Settings.CalloutMinPlayers.ToString();
        CalloutCooldown = manager.Settings.CalloutCooldownSeconds.ToString();
        _loadingCallouts = false;
        _idleEndSeconds = manager.Settings.IdleEndSeconds.ToString("0.#");
        _pollMilliseconds = manager.Settings.PollMilliseconds.ToString();
        _historyBossDays = manager.Settings.HistoryBossDays.ToString();
        _historyTrashDays = manager.Settings.HistoryTrashDays.ToString();

        Voices = AlertAudioService.ListVoices();
        PackRows = [.. PiperVoiceCatalog.Packs.Select(p => new VoicePackRow(p))];
        RefreshPackRows();
        _loadingLanguage = true;
        _selectedLanguage = LanguageChoices.FirstOrDefault(c => c.Code == manager.Settings.LanguageCode)
            ?? LanguageChoices[0];
        _loadingLanguage = false;
        _ttsRate = manager.Settings.TtsRate;
        _voiceDepthAmount = Math.Clamp(1 - manager.Settings.TtsDepth, 0, 0.3);
        _alertVolume = manager.Settings.AlertVolume;
        // Set via the property (not the field) so the piper install check +
        // status line run for the persisted selection too.
        SelectedVoice = Voices.FirstOrDefault(v => v.Id == manager.Settings.TtsVoiceId)
            ?? Voices.FirstOrDefault(v => PiperVoiceCatalog.Find(v.Id) is { } pv && PiperVoiceCatalog.IsInstalled(pv))
            ?? Voices.FirstOrDefault(v => v.DisplayName.Contains("Natural", StringComparison.OrdinalIgnoreCase))
            ?? Voices.FirstOrDefault(v => PiperVoiceCatalog.Find(v.Id) is null);
    }

    [ObservableProperty]
    private bool _calloutsEnabled;

    [ObservableProperty]
    private string _calloutMinPlayers = "3";

    [ObservableProperty]
    private string _calloutCooldown = "12";

    partial void OnCalloutsEnabledChanged(bool value) => PersistCallouts();

    partial void OnCalloutMinPlayersChanged(string value) => PersistCallouts();

    partial void OnCalloutCooldownChanged(string value) => PersistCallouts();

    /// <summary>Callout knobs apply + persist immediately (like audio).</summary>
    private void PersistCallouts()
    {
        if (_loadingCallouts)
            return;
        // Unparseable (mid-typing, cleared box) keeps the CURRENT value —
        // substituting the hardcoded defaults silently reset user settings.
        var minPlayers = int.TryParse(CalloutMinPlayers, out var mp)
            ? Math.Clamp(mp, 1, 24) : _manager.Settings.CalloutMinPlayers;
        var cooldown = int.TryParse(CalloutCooldown, out var cd)
            ? Math.Clamp(cd, 3, 120) : _manager.Settings.CalloutCooldownSeconds;
        _manager.Callouts.MinVictims = minPlayers;
        _manager.Callouts.Cooldown = TimeSpan.FromSeconds(cooldown);
        _manager.Settings = _manager.Settings with
        {
            CalloutsEnabled = CalloutsEnabled,
            CalloutMinPlayers = minPlayers,
            CalloutCooldownSeconds = cooldown,
        };
        _manager.Settings.Save();
    }

    private bool _loadingCallouts;

    [ObservableProperty]
    private string _lexiconStatus = "";

    public string LexiconUrlLabel => _manager.Lexicon.BaseUrl;

    [RelayCommand]
    private void LexiconSyncNow() => _ = _manager.Lexicon.SyncAsync();

    // ---- EQ2Lexicon parse uploads ----

    [ObservableProperty]
    private bool _uploadEnabled;

    [ObservableProperty]
    private string _uploadStatus = "";

    [ObservableProperty]
    private string _uploadTokenState = "";

    private bool _loadingUpload;

    partial void OnUploadEnabledChanged(bool value)
    {
        if (_loadingUpload)
            return;
        _manager.Settings = _manager.Settings with { UploadEnabled = value };
        _manager.Settings.Save();
        ReconfigureUploads();
    }

    private void ReconfigureUploads() =>
        _manager.Uploads.Configure(
            _manager.Settings.LexiconBaseUrl,
            TokenProtector.Unprotect(_manager.Settings.LexiconApiTokenProtected),
            _manager.Settings.UploadEnabled);

    /// <summary>PasswordBox can't data-bind its Password (by design) — the
    /// button passes the box itself and we read + clear it here, so the
    /// token never sits in a bound string property.</summary>
    [RelayCommand]
    private void SaveUploadToken(object? parameter)
    {
        if (parameter is not System.Windows.Controls.PasswordBox box)
            return;
        var token = box.Password.Trim();
        if (token.Length == 0)
        {
            UploadStatus = Loc.Get("SettingsVm_PasteTokenFirst");
            return;
        }
        if (TokenProtector.Protect(token) is not { } blob)
        {
            UploadStatus = Loc.Get("SettingsVm_DpapiUnavailable");
            return;
        }
        box.Clear();
        _manager.Settings = _manager.Settings with { LexiconApiTokenProtected = blob };
        _manager.Settings.Save();
        UploadTokenState = Loc.Get("SettingsVm_TokenSavedState");
        ReconfigureUploads();
        UploadStatus = Loc.Get("SettingsVm_TokenSavedHitTest");
    }

    [RelayCommand]
    private async Task TestUploadToken()
    {
        UploadStatus = Loc.Get("SettingsVm_Testing");
        UploadStatus = await _manager.Uploads.TestTokenAsync();
    }

    [RelayCommand]
    private void ClearUploadToken()
    {
        _manager.Settings = _manager.Settings with { LexiconApiTokenProtected = null, UploadEnabled = false };
        _manager.Settings.Save();
        _loadingUpload = true;
        UploadEnabled = false;
        _loadingUpload = false;
        UploadTokenState = Loc.Get("SettingsVm_NoTokenSaved");
        ReconfigureUploads();
        UploadStatus = Loc.Get("SettingsVm_TokenRemoved");
    }

    [RelayCommand]
    private void TestVoice() =>
        // A Russian model reading the English phrase comes out garbled and
        // reads as a broken install — test it in its own language.
        _manager.Audio.Speak(SelectedVoice?.Id?.StartsWith("piper:ru_RU", StringComparison.Ordinal) == true
            ? "Огненный круг — отойдите от рейда."
            : "Fire circle — move out of the raid.");

    [RelayCommand]
    private void TestChime() =>
        _manager.Audio.PlayChime();

    [RelayCommand]
    private void Save()
    {
        if (!double.TryParse(IdleEndSeconds, out var idle) || idle < 1 || idle > 60)
        {
            Status = Loc.Get("SettingsVm_IdleRange");
            return;
        }
        if (!int.TryParse(PollMilliseconds, out var poll) || poll < 1 || poll > 1000)
        {
            Status = Loc.Get("SettingsVm_PollRange");
            return;
        }
        if (!int.TryParse(HistoryBossDays, out var bossDays) || bossDays < 1 || bossDays > 365)
        {
            Status = Loc.Get("SettingsVm_BossRange");
            return;
        }
        if (!int.TryParse(HistoryTrashDays, out var trashDays) || trashDays < 1 || trashDays > 365)
        {
            Status = Loc.Get("SettingsVm_TrashRange");
            return;
        }
        _manager.Settings = _manager.Settings with
        {
            IdleEndSeconds = idle,
            PollMilliseconds = poll,
            HistoryBossDays = bossDays,
            HistoryTrashDays = trashDays,
            TtsVoiceId = SelectedVoice?.Id,
            TtsRate = TtsRate,
            AlertVolume = AlertVolume,
        };
        _manager.Settings.Save();
        Status = Loc.Get("SettingsVm_Saved");
    }
}
