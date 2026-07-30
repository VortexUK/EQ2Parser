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

    public SettingsViewModel(SourceManager manager)
    {
        _manager = manager;
        _idleEndSeconds = manager.Settings.IdleEndSeconds.ToString("0.#");
        _pollMilliseconds = manager.Settings.PollMilliseconds.ToString();
    }

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
        _manager.Settings = _manager.Settings with { IdleEndSeconds = idle, PollMilliseconds = poll };
        _manager.Settings.Save();
        Status = "Saved. New values apply to sources added from now on.";
    }
}
