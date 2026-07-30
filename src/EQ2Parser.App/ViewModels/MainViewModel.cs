using CommunityToolkit.Mvvm.ComponentModel;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.ViewModels;

public sealed record NavItem(string Label, string Glyph, object Page);

/// <summary>Placeholder page for sections that land in a later slice.</summary>
public sealed record PlaceholderViewModel(string Title, string Message);

/// <summary>The shell: nav rail + current page + the ~100ms coalescing tick
/// that refreshes whichever page is visible.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public SourceManager Manager { get; }
    public LiveViewModel Live { get; }
    public HistoryViewModel History { get; }
    public SourcesViewModel Sources { get; }
    public SettingsViewModel Settings { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty]
    private NavItem _selectedItem;

    public MainViewModel(SourceManager manager)
    {
        Manager = manager;
        Live = new LiveViewModel(manager);
        History = new HistoryViewModel(manager);
        Sources = new SourcesViewModel(manager);
        Settings = new SettingsViewModel(manager);

        NavItems =
        [
            new NavItem("Live", "⚔", Live),
            new NavItem("History", "📜", History),
            new NavItem("Sources", "📄", Sources),
            new NavItem("Triggers", "⚡", new PlaceholderViewModel("Triggers", "Trigger management arrives in the next slice — the engine underneath (ACT XML import, TTS, cooldowns) is already built.")),
            new NavItem("Timers", "⏳", new PlaceholderViewModel("Timers", "Spell timers arrive in the next slice — the engine underneath is already built.")),
            new NavItem("Settings", "⚙", Settings),
        ];
        _selectedItem = NavItems[0];
        Sources.SyncFromManager();
    }

    /// <summary>Dispatcher tick (~100ms): refresh only what's visible.</summary>
    public void Tick()
    {
        if (SelectedItem.Page == Live)
            Live.Refresh();
        else if (SelectedItem.Page == Sources)
            Sources.Refresh();
    }
}
