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
    public RaidViewModel Raid { get; }
    public SettingsViewModel Settings { get; }

    public System.Collections.ObjectModel.ObservableCollection<NavItem> NavItems { get; }

    /// <summary>The Raid nav entry — inserted/removed by
    /// <see cref="SyncRaidNav"/> based on the site entitlement (the
    /// attendance feature set is in limited preview behind the
    /// 'subscriber' role; admins pass).</summary>
    private readonly NavItem _raidNav;

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
        Raid = new RaidViewModel(manager);
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
        _raidNav = new NavItem(Loc.Get("Nav_Raid"), Raid);
        _selectedItem = NavItems[0];
        // Entitlement changes arrive on background threads (whoami probe) —
        // marshal onto the UI thread before touching the nav collection.
        manager.Uploads.AttendanceAccessChanged += () =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(SyncRaidNav);
        SyncRaidNav();
        Sources.SyncFromManager();
    }

    /// <summary>Show the Raid tab only while the configured token's account
    /// holds the attendance-preview entitlement (subscriber role / admin).</summary>
    private void SyncRaidNav()
    {
        var show = Manager.Uploads.AttendanceAccess == true;
        var present = NavItems.Contains(_raidNav);
        if (show && !present)
        {
            NavItems.Insert(NavItems.Count - 1, _raidNav); // before Settings
        }
        else if (!show && present)
        {
            NavItems.Remove(_raidNav);
            _tabHistory.RemoveAll(n => ReferenceEquals(n, _raidNav));
            if (ReferenceEquals(SelectedItem, _raidNav))
                SelectedItem = NavItems[0];
        }
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
        Manager.Uploads.TickAttendance(Manager, DateTimeOffset.Now);
        if (SelectedItem.Page == Main)
            Main.Refresh();
        else if (SelectedItem.Page == Sources)
            Sources.Refresh();
        else if (SelectedItem.Page == Timers)
            Timers.Refresh();
        else if (SelectedItem.Page == Raid)
            Raid.Refresh();
    }
}
