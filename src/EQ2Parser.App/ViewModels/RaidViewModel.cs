using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Persistence;
using EQ2Parser.Core.Raid;
using EQ2Parser.Core.Upload;

namespace EQ2Parser.App.ViewModels;

/// <summary>One row in the raid-tracking lists. Include is only meaningful
/// on the sit-out list (gates who goes into the DKP sit-out commands).</summary>
public sealed partial class RaidRow : ObservableObject
{
    public required string Name { get; init; }
    public string? Class { get; init; }
    public string LastSeen { get; init; } = "";

    /// <summary>In raid but provably absent from the guild who — can't
    /// receive guild points themselves (still tracked for attendance).</summary>
    public bool NotInGuild { get; init; }

    /// <summary>The raid main this row's DKP is redirected to (from the
    /// site's mains map), when it differs from the row's own name. A
    /// mapped row stays in the DKP file even when NotInGuild — the award
    /// line targets the main, who CAN receive points. Two rows sharing a
    /// target (dual-boxed characters of one player) are awarded once.</summary>
    public string? DkpTarget { get; init; }

    /// <summary>Row annotation: "DKP → Main" beats "not in guild — no DKP"
    /// (a redirected row still banks points via its main).</summary>
    public string StatusTag =>
        DkpTarget is not null ? Loc.Format("Raid_DkpGoesTo", DkpTarget)
        : NotInGuild ? Loc.Get("Raid_NotInGuild")
        : "";

    [ObservableProperty]
    private bool _include = true;
}

/// <summary>
/// The Raid tab: live raid roster + sit-out list accumulated by
/// <see cref="RaidRosterTracker"/> (see its docs for the log signals), plus
/// the two /do_file_commands files this app writes into the EQ2 install dir —
/// the roster-refresh macro (/who pair) and the DKP award file. DKP lives
/// in-game (guild points); awards are fire-and-forget (no log feedback).
/// </summary>
public sealed partial class RaidViewModel : ObservableObject
{
    private readonly SourceManager _manager;
    private volatile bool _dirty = true;

    public RaidViewModel(SourceManager manager)
    {
        _manager = manager;
        manager.RaidRoster.RosterChanged += () => _dirty = true;
        manager.DkpProgress.PressDetected += OnDkpPress;
        _dkpPoints = manager.Settings.RaidDkpPoints.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _dkpReason = Loc.Get("Raid_DefaultReason");
    }

    // ── DKP award queue (the game applies ONE points command per macro
    // press; the marker line tells us how many remain — see DkpCommandFile) ──
    private readonly object _queueGate = new();
    private List<string> _awardQueue = [];
    private int _awardTotal;

    /// <summary>PressDetected handler — pump thread. Pops the applied
    /// command(s), rewrites the file to the remainder, updates Status
    /// (WPF marshals scalar binding updates).</summary>
    private void OnDkpPress(int failures)
    {
        string contents;
        string status;
        lock (_queueGate)
        {
            if (_awardQueue.Count == 0)
                return; // stray press after completion — marker-only file, nothing to do
            var (remaining, applied) = DkpCommandFile.AdvanceQueue(_awardQueue, failures);
            if (applied == 0)
            {
                Status = Loc.Get("Raid_DkpThrottled");
                return;
            }
            _awardQueue = remaining;
            contents = DkpCommandFile.BuildQueueFile(remaining);
            status = remaining.Count == 0
                ? Loc.Format("Raid_DkpAllDone", _awardTotal)
                : Loc.Format("Raid_DkpProgress", _awardTotal - remaining.Count, _awardTotal);
        }
        if (WriteCommandFile(contents, _manager.Settings.RaidDkpFileName))
            Status = status; // progress line beats the plain file-written line
    }

    public System.Collections.ObjectModel.ObservableCollection<RaidRow> InRaid { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<RaidRow> SittingOut { get; } = [];

    [ObservableProperty]
    private string _inRaidHeader = "";

    [ObservableProperty]
    private string _sitOutHeader = "";

    [ObservableProperty]
    private string _dkpPoints = "5";

    [ObservableProperty]
    private string _dkpReason = "";

    [ObservableProperty]
    private string _status = "";

    /// <summary>Whether the site's character→main map is loaded (DKP then
    /// lands on mains even for players raiding on alts).</summary>
    [ObservableProperty]
    private string _mainsStatus = "";

    /// <summary>Names the officer un-ticked — survives roster refreshes.</summary>
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mains map used for the last rebuild — a new map arriving
    /// from the site (no roster change involved) must also refresh rows.</summary>
    private IReadOnlyDictionary<string, string>? _lastMains;

    /// <summary>Called from the shell tick while this tab is visible.</summary>
    public void Refresh()
    {
        if (!_dirty && ReferenceEquals(_lastMains, _manager.Uploads.RaidMains))
            return;
        _dirty = false;
        _lastMains = _manager.Uploads.RaidMains;
        var snapshot = _manager.RaidRoster.Snapshot();
        var inRaid = snapshot.Where(m => m.InRaid).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var sitOut = snapshot.Where(m => m is { InRaid: false, Online: true })
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

        Rebuild(InRaid, inRaid, includeToggles: false);
        Rebuild(SittingOut, sitOut, includeToggles: true);
        InRaidHeader = Loc.Format("Raid_InRaidHeader", inRaid.Count);
        SitOutHeader = Loc.Format("Raid_SitOutHeader", sitOut.Count);
        MainsStatus = _manager.Uploads.RaidMains is { Count: > 0 } mains
            ? Loc.Format("Raid_MainsActive", mains.Count)
            : Loc.Get("Raid_MainsInactive");
    }

    private void Rebuild(System.Collections.ObjectModel.ObservableCollection<RaidRow> target, List<RaidMemberState> rows, bool includeToggles)
    {
        target.Clear();
        foreach (var m in rows)
        {
            string? dkpTarget = null;
            if (_lastMains is { } mains && mains.TryGetValue(m.Name, out var main)
                && !string.Equals(main, m.Name, StringComparison.OrdinalIgnoreCase))
            {
                dkpTarget = main;
            }
            var row = new RaidRow
            {
                Name = m.Name,
                Class = m.Class, // /who detail rows carry it; blank otherwise
                LastSeen = (m.RaidLastSeen ?? m.OnlineLastSeen)?.LocalDateTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                NotInGuild = m.InGuild == false,
                DkpTarget = dkpTarget,
                Include = !includeToggles || !_excluded.Contains(m.Name),
            };
            if (includeToggles)
            {
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(RaidRow.Include))
                        return;
                    if (row.Include)
                        _excluded.Remove(row.Name);
                    else
                        _excluded.Add(row.Name);
                };
            }
            target.Add(row);
        }
    }

    // ── command files ───────────────────────────────────────────────────────

    private string? ResolveTargetDir()
    {
        if (!string.IsNullOrWhiteSpace(_manager.Settings.RaidCommandDirOverride))
            return _manager.Settings.RaidCommandDirOverride;
        foreach (var source in _manager.Sources)
        {
            if (LogPaths.ParseInstallDir(source.Path) is { } dir)
                return dir;
        }
        return null;
    }

    private bool WriteCommandFile(string contents, string fileName)
    {
        var dir = ResolveTargetDir();
        if (dir is null)
        {
            Status = Loc.Get("Raid_NoInstallDir");
            return false;
        }
        var path = Path.Combine(dir, fileName);
        try
        {
            PersistedJsonFile.SaveText(path, contents);
            Status = Loc.Format("Raid_FileWritten", path);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Program Files installs aren't user-writable — point the user at
            // the folder override setting instead of failing silently.
            Status = Loc.Format("Raid_FileError", ex.Message);
            return false;
        }
    }

    [RelayCommand]
    private void WriteRefreshFile() => WriteCommandFile(DkpCommandFile.BuildRefresh(), _manager.Settings.RaidListFileName);

    [RelayCommand]
    private void WriteDkpFile()
    {
        if (!int.TryParse(DkpPoints, out var points) || points is < 1 or > 1000)
        {
            Status = Loc.Get("Raid_BadPoints");
            return;
        }
        // A partially-applied batch is the dangerous case: rewriting the full
        // list would re-award everyone who already got their points on the
        // next presses. Fresh or completed queues reset silently.
        int pending, total;
        lock (_queueGate)
        {
            pending = _awardQueue.Count;
            total = _awardTotal;
        }
        if (pending > 0 && pending < total)
        {
            var confirm = System.Windows.MessageBox.Show(
                Loc.Format("Raid_DkpResetWarn", total - pending, total),
                Loc.Get("Raid_DkpResetTitle"),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes)
                return;
        }
        // Not-in-guild members can't receive guild points — keep them out of
        // the file UNLESS their DKP is redirected to an in-guild main (the
        // award line then targets the main). Still tracked for attendance.
        var raidNames = InRaid.Where(r => !r.NotInGuild || r.DkpTarget is not null).Select(r => r.Name).ToList();
        var sitOuts = SittingOut.Where(r => r.Include).Select(r => r.Name).ToList();
        var commands = DkpCommandFile.BuildAwardCommands(points, DkpReason, raidNames, sitOuts, _manager.Uploads.RaidMains);
        lock (_queueGate)
        {
            _awardQueue = commands;
            _awardTotal = commands.Count;
        }
        if (WriteCommandFile(DkpCommandFile.BuildQueueFile(commands), _manager.Settings.RaidDkpFileName)
            && commands.Count > 0)
        {
            Status = Loc.Format("Raid_DkpQueued", commands.Count);
        }
        // Persist the chosen points as the new default.
        _manager.Settings = _manager.Settings with { RaidDkpPoints = points };
        _manager.Settings.Save();
    }

    /// <summary>The exact in-game macro commands (reflect the configured
    /// file names) with one-click copy for macro creation.</summary>
    public string RosterMacroCommand => $"/do_file_commands {_manager.Settings.RaidListFileName}";

    public string DkpMacroCommand => $"/do_file_commands {_manager.Settings.RaidDkpFileName}";

    [RelayCommand]
    private void CopyRosterMacro() => CopyToClipboard(RosterMacroCommand);

    [RelayCommand]
    private void CopyDkpMacro() => CopyToClipboard(DkpMacroCommand);

    private void CopyToClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            Status = Loc.Format("Raid_Copied", text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard briefly owned by another app — harmless, retry works.
        }
    }

    [RelayCommand]
    private void StartNewSession()
    {
        _excluded.Clear();
        // Drop any lingering award queue and neutralise the file on disk so
        // a stale batch can't be fired into the new night by accident.
        bool hadQueue;
        lock (_queueGate)
        {
            hadQueue = _awardQueue.Count > 0;
            _awardQueue = [];
            _awardTotal = 0;
        }
        if (hadQueue)
            WriteCommandFile(DkpCommandFile.BuildQueueFile([]), _manager.Settings.RaidDkpFileName);
        _manager.RaidRoster.StartNewSession(DateTimeOffset.Now);
        Status = Loc.Get("Raid_SessionCleared");
        _dirty = true;
    }
}
