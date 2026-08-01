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
    public TimersViewModel Timers { get; }
    public OverlaysViewModel Overlays { get; }
    public SettingsViewModel Settings { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty]
    private NavItem _selectedItem;

    public MainViewModel(SourceManager manager, OverlayController overlay)
    {
        Manager = manager;
        Main = new MainParseViewModel(manager);
        Sources = new SourcesViewModel(manager);
        Triggers = new TriggersViewModel(manager);
        Timers = new TimersViewModel(manager);
        Overlays = new OverlaysViewModel(manager, overlay);
        Settings = new SettingsViewModel(manager);

        NavItems =
        [
            new NavItem("Main", Main),
            new NavItem("Sources", Sources),
            new NavItem("Triggers", Triggers),
            new NavItem("Timers", Timers),
            new NavItem("Overlays", Overlays),
            new NavItem("Settings", Settings),
        ];
        _selectedItem = NavItems[0];
        Sources.SyncFromManager();
    }

    /// <summary>Dispatcher tick (~100ms): the timer clock always advances
    /// (warnings/expiries fire whatever page is visible); page refresh only
    /// for what's on screen.</summary>
    public void Tick()
    {
        Manager.SpellTimers.Tick(DateTimeOffset.Now);
        Manager.Callouts.Tick(DateTimeOffset.Now);
        if (SelectedItem.Page == Main)
            Main.Refresh();
        else if (SelectedItem.Page == Sources)
            Sources.Refresh();
        else if (SelectedItem.Page == Timers)
            Timers.Refresh();
    }
}
