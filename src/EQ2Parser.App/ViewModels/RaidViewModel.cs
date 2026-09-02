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
        _dkpPoints = manager.Settings.RaidDkpPoints.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _dkpReason = Loc.Get("Raid_DefaultReason");
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

    /// <summary>Names the officer un-ticked — survives roster refreshes.</summary>
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called from the shell tick while this tab is visible.</summary>
    public void Refresh()
    {
        if (!_dirty)
            return;
        _dirty = false;
        var snapshot = _manager.RaidRoster.Snapshot();
        var inRaid = snapshot.Where(m => m.InRaid).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var sitOut = snapshot.Where(m => m is { InRaid: false, Online: true })
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

        Rebuild(InRaid, inRaid, includeToggles: false);
        Rebuild(SittingOut, sitOut, includeToggles: true);
        InRaidHeader = Loc.Format("Raid_InRaidHeader", inRaid.Count);
        SitOutHeader = Loc.Format("Raid_SitOutHeader", sitOut.Count);
    }

    private void Rebuild(System.Collections.ObjectModel.ObservableCollection<RaidRow> target, List<RaidMemberState> rows, bool includeToggles)
    {
        target.Clear();
        foreach (var m in rows)
        {
            var row = new RaidRow
            {
                Name = m.Name,
                Class = m.Class, // /who detail rows carry it; blank otherwise
                LastSeen = (m.RaidLastSeen ?? m.OnlineLastSeen)?.LocalDateTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "",
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

    private void WriteCommandFile(string contents, string fileName)
    {
        var dir = ResolveTargetDir();
        if (dir is null)
        {
            Status = Loc.Get("Raid_NoInstallDir");
            return;
        }
        var path = Path.Combine(dir, fileName);
        try
        {
            PersistedJsonFile.SaveText(path, contents);
            Status = Loc.Format("Raid_FileWritten", path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Program Files installs aren't user-writable — point the user at
            // the folder override setting instead of failing silently.
            Status = Loc.Format("Raid_FileError", ex.Message);
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
        var sitOuts = SittingOut.Where(r => r.Include).Select(r => r.Name).ToList();
        WriteCommandFile(DkpCommandFile.BuildAward(points, DkpReason, sitOuts), _manager.Settings.RaidDkpFileName);
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
        _manager.RaidRoster.StartNewSession(DateTimeOffset.Now);
        Status = Loc.Get("Raid_SessionCleared");
        _dirty = true;
    }
}
