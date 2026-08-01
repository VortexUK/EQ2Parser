using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private string _actionLabel = "Download";

    [ObservableProperty]
    private bool _busy;
}

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

    public string TtsRateLabel => $"{TtsRate:0.0}×";
    public string AlertVolumeLabel => $"{AlertVolume:P0}";

    partial void OnTtsRateChanged(double value)
    {
        _manager.Audio.SpeakingRate = value;
        OnPropertyChanged(nameof(TtsRateLabel));
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
    /// restart. The Save button remains for the validated parsing fields.</summary>
    private void PersistAudio()
    {
        _manager.Settings = _manager.Settings with
        {
            TtsVoiceId = SelectedVoice?.Id,
            TtsRate = TtsRate,
            AlertVolume = AlertVolume,
        };
        _manager.Settings.Save();
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
            VoiceStatus = "Neural voice ready — offline from here on.";
            return;
        }
        if (PiperVoiceCatalog.PackFor(neural) is { } pack)
        {
            VoiceStatus = $"This voice needs the {pack.SizeMb} MB pack below — downloading it now.";
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
        try
        {
            _manager.Audio.UnloadNeuralModel();
            PiperVoiceCatalog.RemovePack(row.Pack);
            RefreshPackRows();
            if (PiperVoiceCatalog.Find(SelectedVoice?.Id) is { } current && current.Archive == row.Pack.Archive)
                VoiceStatus = "Pack removed — alerts fall back to a Windows voice until it's downloaded again.";
        }
        catch (Exception ex)
        {
            row.Status = $"Couldn't remove ({ex.Message}) — try again in a moment.";
        }
    }

    private async Task DownloadPackAsync(VoicePackRow row)
    {
        if (_downloadingArchive is not null)
        {
            row.Status = "Another download is running — try again when it finishes.";
            return;
        }
        _downloadingArchive = row.Pack.Archive;
        row.Busy = true;
        try
        {
            var progress = new Progress<double>(p => row.Status = $"Downloading — {p:P0} of ~{row.Pack.SizeMb} MB…");
            await PiperVoiceCatalog.DownloadAsync(row.Pack.PackVoices[0], progress, CancellationToken.None);
            RefreshPackRows();
            if (PiperVoiceCatalog.Find(SelectedVoice?.Id) is { } current && current.Archive == row.Pack.Archive)
                VoiceStatus = "Neural voice installed — hit Test. Alerts use it from now on.";
        }
        catch (Exception ex)
        {
            row.Status = $"Download failed ({ex.Message}) — hit Download to retry.";
            row.ActionLabel = "Download";
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
                row.Status = $"Installed · {mb:0} MB on disk";
                row.ActionLabel = "Remove";
            }
            else
            {
                row.Status = $"{row.Pack.SizeMb} MB download";
                row.ActionLabel = "Download";
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
        _ttsRate = manager.Settings.TtsRate;
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
        var minPlayers = int.TryParse(CalloutMinPlayers, out var mp) ? Math.Clamp(mp, 1, 24) : 3;
        var cooldown = int.TryParse(CalloutCooldown, out var cd) ? Math.Clamp(cd, 3, 120) : 12;
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

    [RelayCommand]
    private void TestVoice() =>
        _manager.Audio.Speak("Fire circle — move out of the raid.");

    [RelayCommand]
    private void TestChime() =>
        _manager.Audio.PlayChime();

    [RelayCommand]
    private void Save()
    {
        if (!double.TryParse(IdleEndSeconds, out var idle) || idle < 1 || idle > 60)
        {
            Status = "Idle timeout must be 1–60 seconds.";
            return;
        }
        if (!int.TryParse(PollMilliseconds, out var poll) || poll < 1 || poll > 1000)
        {
            Status = "Poll interval must be 1–1000 ms.";
            return;
        }
        if (!int.TryParse(HistoryBossDays, out var bossDays) || bossDays < 1 || bossDays > 365)
        {
            Status = "Boss history must load 1–365 days (bosses stay archived beyond that).";
            return;
        }
        if (!int.TryParse(HistoryTrashDays, out var trashDays) || trashDays < 1 || trashDays > 365)
        {
            Status = "Trash retention must be 1–365 days.";
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
        Status = "Saved. Parsing values apply to sources added from now on; audio applies immediately.";
    }
}
