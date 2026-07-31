using CommunityToolkit.Mvvm.ComponentModel;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.ViewModels;

public sealed record NavItem(string Label, object Page);

/// <summary>Placeholder page for sections that land in a later slice.</summary>
public sealed record PlaceholderViewModel(string Title, string Message);

/// <summary>The shell: ACT-style top tabs + current page + the ~100ms
/// coalescing tick that refreshes whichever page is visible.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public SourceManager Manager { get; }
    public MainParseViewModel Main { get; }
    public SourcesViewModel Sources { get; }
    public TriggersViewModel Triggers { get; }
    public SettingsViewModel Settings { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty]
    private NavItem _selectedItem;

    public MainViewModel(SourceManager manager)
    {
        Manager = manager;
        Main = new MainParseViewModel(manager);
        Sources = new SourcesViewModel(manager);
        Triggers = new TriggersViewModel(manager);
        Settings = new SettingsViewModel(manager);

        NavItems =
        [
            new NavItem("Main", Main),
            new NavItem("Sources", Sources),
            new NavItem("Triggers", Triggers),
            new NavItem("Timers", new PlaceholderViewModel("Timers", "Spell timers arrive in the next slice — the engine underneath is already built.")),
            new NavItem("Settings", Settings),
        ];
        _selectedItem = NavItems[0];
        Sources.SyncFromManager();
    }

    /// <summary>Dispatcher tick (~100ms): refresh only what's visible.</summary>
    public void Tick()
    {
        if (SelectedItem.Page == Main)
            Main.Refresh();
        else if (SelectedItem.Page == Sources)
            Sources.Refresh();
    }
}
