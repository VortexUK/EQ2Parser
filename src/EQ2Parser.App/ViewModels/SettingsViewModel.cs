using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SourceManager _manager;

    [ObservableProperty]
    private string _idleEndSeconds;

    [ObservableProperty]
    private string _pollMilliseconds;

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

    private string? _downloadingKey;

    public string TtsRateLabel => $"{TtsRate:0.0}×";
    public string AlertVolumeLabel => $"{AlertVolume:P0}";

    partial void OnTtsRateChanged(double value)
    {
        _manager.Audio.SpeakingRate = value;
        OnPropertyChanged(nameof(TtsRateLabel));
    }

    partial void OnAlertVolumeChanged(double value)
    {
        _manager.Audio.Volume = value;
        OnPropertyChanged(nameof(AlertVolumeLabel));
    }

    partial void OnSelectedVoiceChanged(TtsVoice? value)
    {
        _manager.Audio.VoiceId = value?.Id;
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
        _ = DownloadVoiceAsync(neural);
    }

    private async Task DownloadVoiceAsync(PiperVoice neural)
    {
        if (_downloadingKey is not null)
        {
            VoiceStatus = $"Still downloading another voice — {neural.DisplayName} will need re-selecting after.";
            return;
        }
        _downloadingKey = neural.Key;
        VoiceStatus = $"Downloading {neural.DisplayName} ({neural.SizeMb} MB)…";
        try
        {
            var progress = new Progress<double>(p =>
                VoiceStatus = $"Downloading {neural.DisplayName} — {p:P0} of ~{neural.SizeMb} MB…");
            await PiperVoiceCatalog.DownloadAsync(neural, progress, CancellationToken.None);
            VoiceStatus = $"{neural.DisplayName} installed — hit Test. Alerts use it from now on.";
        }
        catch (Exception ex)
        {
            VoiceStatus = $"Download failed ({ex.Message}). Alerts fall back to a Windows voice; re-select the neural voice to retry.";
        }
        finally
        {
            _downloadingKey = null;
        }
    }

    public SettingsViewModel(SourceManager manager)
    {
        _manager = manager;
        _idleEndSeconds = manager.Settings.IdleEndSeconds.ToString("0.#");
        _pollMilliseconds = manager.Settings.PollMilliseconds.ToString();

        Voices = AlertAudioService.ListVoices();
        _ttsRate = manager.Settings.TtsRate;
        _alertVolume = manager.Settings.AlertVolume;
        // Set via the property (not the field) so the piper install check +
        // status line run for the persisted selection too.
        SelectedVoice = Voices.FirstOrDefault(v => v.Id == manager.Settings.TtsVoiceId)
            ?? Voices.FirstOrDefault(v => PiperVoiceCatalog.Find(v.Id) is { } pv && PiperVoiceCatalog.IsInstalled(pv))
            ?? Voices.FirstOrDefault(v => v.DisplayName.Contains("Natural", StringComparison.OrdinalIgnoreCase))
            ?? Voices.FirstOrDefault(v => PiperVoiceCatalog.Find(v.Id) is null);
    }

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
        _manager.Settings = _manager.Settings with
        {
            IdleEndSeconds = idle,
            PollMilliseconds = poll,
            TtsVoiceId = SelectedVoice?.Id,
            TtsRate = TtsRate,
            AlertVolume = AlertVolume,
        };
        _manager.Settings.Save();
        Status = "Saved. Parsing values apply to sources added from now on; audio applies immediately.";
    }
}
