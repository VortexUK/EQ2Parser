using CommunityToolkit.Mvvm.ComponentModel;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.ViewModels;

public sealed record NavItem(string Label, object Page);

/// <summary>The shell: top tabs + current page + the ~100ms
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
        Overlays = new OverlaysViewModel(overlay, manager);
        Settings = new SettingsViewModel(manager);

        NavItems =
        [
            new NavItem(Loc.Get("Nav_Main"), Main),
            new NavItem(Loc.Get("Nav_Sources"), Sources),
            new NavItem(Loc.Get("Nav_Triggers"), Triggers),
            new NavItem(Loc.Get("Nav_Timers"), Timers),
            new NavItem(Loc.Get("Nav_Overlays"), Overlays),
            new NavItem(Loc.Get("Nav_Settings"), Settings),
        ];
        _selectedItem = NavItems[0];
        Sources.SyncFromManager();
    }

    // ── Back navigation (mouse XButton1) ────────────────────────────────────

    private readonly List<NavItem> _tabHistory = [];
    private bool _navigatingBack;

    partial void OnSelectedItemChanged(NavItem? oldValue, NavItem newValue)
    {
        if (_navigatingBack || oldValue is null || ReferenceEquals(oldValue, newValue))
            return;
        _tabHistory.Add(oldValue);
        if (_tabHistory.Count > 20)
            _tabHistory.RemoveAt(0);
    }

    /// <summary>Context-aware back: pop a drill level on the Main page
    /// first; otherwise return to the previously visited tab.</summary>
    public bool NavigateBack()
    {
        if (SelectedItem.Page == Main && Main.TryNavigateBack())
            return true;
        if (_tabHistory.Count == 0)
            return false;
        var target = _tabHistory[^1];
        _tabHistory.RemoveAt(_tabHistory.Count - 1);
        _navigatingBack = true;
        SelectedItem = target;
        _navigatingBack = false;
        return true;
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
