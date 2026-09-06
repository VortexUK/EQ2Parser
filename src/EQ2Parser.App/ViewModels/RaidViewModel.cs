using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Persistence;
using EQ2Parser.Core.Raid;
using EQ2Parser.Core.Upload;

namespace EQ2Parser.App.ViewModels;

/// <summary>One dropped item in the loot list. Buyer/Cost are officer-
/// editable; Charged is the confirm column — it flips only when the row's
/// deduction has provably applied in game (its press landed), and charged
/// rows are excluded from any rewrite so a row can never bill twice.</summary>
public sealed partial class LootRow : ObservableObject
{
    public required int Id { get; init; }
    public required string ItemName { get; init; }

    [ObservableProperty]
    private string _boss = "";

    /// <summary>Who pays — auto-filled from the "X loots …" line, editable
    /// (leader-looted items get handed out by trade, which the log never
    /// sees).</summary>
    [ObservableProperty]
    private string _buyer = "";

    [ObservableProperty]
    private string _cost = "";

    [ObservableProperty]
    private bool _charged;

    /// <summary>True once the buyer has touched the field — auto-fill from
    /// the loots line must never overwrite a hand-entered name.</summary>
    public bool BuyerEdited { get; set; }

    /// <summary>Set by the view-model around programmatic Buyer writes so
    /// auto-fill doesn't count as a user edit.</summary>
    public bool Suppress { get; set; }

    partial void OnBuyerChanged(string value)
    {
        _ = value;
        if (!Suppress)
            BuyerEdited = true;
    }

    // Quick-set buttons: DKP costs are near-always multiples of 10, so ±10
    // beats typing. Unparseable text resets to 0 before stepping.
    [RelayCommand]
    private void CostUp() => BumpCost(+10);

    [RelayCommand]
    private void CostDown() => BumpCost(-10);

    private void BumpCost(int delta)
    {
        _ = int.TryParse(Cost, out var current);
        Cost = Math.Max(0, current + delta).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}


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
/// the three /do_file_commands files this app writes into the EQ2 install
/// dir — the roster-refresh macro (/who pair), the DKP award file, and the
/// loot-charge file (each points file has its own press-until-done queue).
/// DKP lives in-game (guild points); successes are silent — progress comes
/// from each file's throttle-count + marker signal.
/// </summary>
public sealed partial class RaidViewModel : ObservableObject
{
    private readonly SourceManager _manager;
    private volatile bool _dirty = true;

    public RaidViewModel(SourceManager manager)
    {
        _manager = manager;
        manager.RaidRoster.RosterChanged += () => _dirty = true;
        manager.Loot.LootChanged += () => _lootDirty = true;
        manager.DkpProgress.PressDetected += OnDkpPress;
        _dkpPoints = manager.Settings.RaidDkpPoints.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _dkpReason = Loc.Get("Raid_DefaultReason");
        _lootMinBid = manager.Settings.RaidLootMinBid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Default DKP cost pre-filled on newly-dropped items (the
    /// guild's minimum bid). Persisted as it changes.</summary>
    [ObservableProperty]
    private string _lootMinBid;

    partial void OnLootMinBidChanged(string value)
    {
        if (!int.TryParse(value, out var bid) || bid is < 0 or > 100000)
            return;
        _manager.Settings = _manager.Settings with { RaidLootMinBid = bid };
        AppSettings.SaveSoon(() => _manager.Settings);
    }

    // ── press-until-done queues (the game applies ONE points command per
    // macro press; each file's own marker line says how many remain). The
    // award queue is set explicitly by "Write DKP file"; the loot queue is
    // DERIVED — SyncLootFile keeps the file mirroring the editable rows,
    // so there is no write button. _queueGate covers queue state AND the
    // synced-file writes (press handler on the pump thread vs the UI tick). ──
    private readonly object _queueGate = new();
    private List<string> _awardQueue = [];
    private int _awardTotal;
    private List<string> _lootQueue = [];

    /// <summary>The LootRow behind each queued loot command, in file order —
    /// as presses land, rows are confirmed charged positionally.</summary>
    private List<LootRow> _lootQueueRows = [];

    /// <summary>Last text successfully synced to the loot file — the
    /// no-op-write comparison for the every-tick sync.</summary>
    private string? _lootFileText;
    private string? _lootFileFailed;

    /// <summary>PressDetected handler — pump thread. Routes on the marker
    /// (award file vs loot file), pops the applied command(s), rewrites
    /// that file to the remainder, updates Status (WPF marshals scalar
    /// binding updates).</summary>
    private void OnDkpPress(string marker, int failures)
    {
        if (marker == DkpCommandFile.LootMarkerCommand)
        {
            OnLootPress(failures);
            return;
        }
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

    private void OnLootPress(int failures)
    {
        string status;
        lock (_queueGate)
        {
            if (_lootQueue.Count == 0)
                return;
            var (remaining, applied) = DkpCommandFile.AdvanceQueue(_lootQueue, failures);
            if (applied == 0)
            {
                Status = Loc.Get("Raid_DkpThrottled");
                return;
            }
            // Flip the confirm column INSIDE the gate: the deduction has
            // provably run in game, and the next SyncLootFile (which also
            // takes the gate) must already see the row as charged — a stale
            // read would write the applied command back into the file.
            foreach (var row in _lootQueueRows.Take(applied))
                row.Charged = true;
            _lootQueue = remaining;
            _lootQueueRows = [.. _lootQueueRows.Skip(applied)];
            // Rewrite immediately (not on the next tick) so a rapid second
            // press can't re-run the just-applied command.
            var text = DkpCommandFile.BuildQueueFile(remaining, DkpCommandFile.LootMarkerCommand);
            if (TrySyncFile(text, _manager.Settings.RaidLootFileName, ref _lootFileFailed))
                _lootFileText = text;
            status = remaining.Count == 0
                ? Loc.Get("Raid_LootAllDone")
                : Loc.Format("Raid_LootProgress", remaining.Count);
        }
        Status = status;
    }

    public System.Collections.ObjectModel.ObservableCollection<RaidRow> InRaid { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<RaidRow> SittingOut { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<LootRow> Loot { get; } = [];

    private volatile bool _lootDirty = true;

    [ObservableProperty]
    private string _lootHeader = "";

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

    /// <summary>Called from the shell tick EVERY tick (not just while the
    /// tab is visible) — the auto-synced command files must stay current
    /// while the officer watches the parse on another tab.</summary>
    public void Refresh()
    {
        RefreshLoot();
        SyncRosterFile();
        SyncLootFile();
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

    /// <summary>Merge the loot snapshot into the editable rows — never a
    /// rebuild: Buyer/Cost/Charged are officer edits and must survive.</summary>
    private void RefreshLoot()
    {
        if (!_lootDirty)
            return;
        _lootDirty = false;
        var by_id = Loot.ToDictionary(r => r.Id);
        foreach (var item in _manager.Loot.Snapshot())
        {
            if (_deletedLootIds.Contains(item.Id))
                continue; // officer deleted the row (materials etc.) — stay gone
            if (by_id.TryGetValue(item.Id, out var row))
            {
                if (item.Boss is not null && row.Boss.Length == 0)
                    row.Boss = item.Boss;
                if (item.LootedBy is not null && !row.BuyerEdited && row.Buyer.Length == 0)
                {
                    row.Suppress = true;
                    row.Buyer = item.LootedBy;
                    row.Suppress = false;
                }
            }
            else
            {
                var created = new LootRow
                {
                    Suppress = true,
                    Id = item.Id,
                    ItemName = item.ItemName,
                    Boss = item.Boss ?? "",
                    Buyer = item.LootedBy ?? "",
                    // New drops start at the guild's minimum bid.
                    Cost = int.TryParse(LootMinBid, out var bid) && bid > 0
                        ? bid.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "",
                };
                created.Suppress = false;
                Loot.Add(created);
            }
        }
        LootHeader = Loc.Format("Raid_LootHeader", Loot.Count);
    }

    /// <summary>Rows the officer deleted (not actual loot — collection
    /// pieces, materials); the snapshot merge must never re-add them.</summary>
    private readonly HashSet<int> _deletedLootIds = [];

    [RelayCommand]
    private void DeleteLoot(LootRow? row)
    {
        if (row is null)
            return;
        _deletedLootIds.Add(row.Id);
        Loot.Remove(row);
        LootHeader = Loc.Format("Raid_LootHeader", Loot.Count);
        SyncLootFile();
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

    /// <summary>Quiet write for the auto-synced files: no success status
    /// line (they rewrite constantly). A missing install dir is the normal
    /// transient before sources restore — skip silently and retry next
    /// tick; a real IO failure surfaces once and stops retrying until the
    /// desired contents change.</summary>
    private bool TrySyncFile(string contents, string fileName, ref string? lastFailed)
    {
        var dir = ResolveTargetDir();
        if (dir is null || contents == lastFailed)
            return false;
        try
        {
            PersistedJsonFile.SaveText(Path.Combine(dir, fileName), contents);
            lastFailed = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            lastFailed = contents;
            Status = Loc.Format("Raid_FileError", ex.Message);
            return false;
        }
    }

    private bool _rosterFileSynced;
    private string? _rosterFileFailed;

    /// <summary>The roster /who file is static — write it once per session
    /// as soon as the install dir resolves (no button needed).</summary>
    private void SyncRosterFile()
    {
        if (_rosterFileSynced)
            return;
        _rosterFileSynced = TrySyncFile(DkpCommandFile.BuildRefresh(), _manager.Settings.RaidListFileName, ref _rosterFileFailed);
    }

    /// <summary>Keep the loot file mirroring the rows: every unbilled row
    /// with a plausible buyer and a positive cost, in list order, plus the
    /// loot marker. Runs every tick — the text comparison makes the
    /// steady-state a no-op. Filter matches BuildLootCommands EXACTLY so
    /// the queue confirms rows positionally.</summary>
    private void SyncLootFile()
    {
        lock (_queueGate)
        {
            var rows = Loot
                .Where(r => !r.Charged
                    && Core.Combat.Swing.LooksLikePlayer(r.Buyer.Trim())
                    && int.TryParse(r.Cost, out var c) && c > 0)
                .ToList();
            var charges = rows
                .Select(r => new LootCharge(r.Buyer.Trim(), int.Parse(r.Cost, System.Globalization.CultureInfo.InvariantCulture), r.ItemName))
                .ToList();
            var commands = DkpCommandFile.BuildLootCommands(charges, _manager.Uploads.RaidMains);
            var text = DkpCommandFile.BuildQueueFile(commands, DkpCommandFile.LootMarkerCommand);
            if (text == _lootFileText)
                return;
            if (!TrySyncFile(text, _manager.Settings.RaidLootFileName, ref _lootFileFailed))
                return;
            _lootFileText = text;
            _lootQueue = commands;
            _lootQueueRows = rows;
        }
    }

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

        var commands = DkpCommandFile.BuildAwardCommands(
            points, DkpReason, raidNames, sitOuts, _manager.Uploads.RaidMains);
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

    public string LootMacroCommand => $"/do_file_commands {_manager.Settings.RaidLootFileName}";

    [RelayCommand]
    private void CopyRosterMacro() => CopyToClipboard(RosterMacroCommand);

    [RelayCommand]
    private void CopyDkpMacro() => CopyToClipboard(DkpMacroCommand);

    [RelayCommand]
    private void CopyLootMacro() => CopyToClipboard(LootMacroCommand);

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
        _manager.Loot.StartNewSession();
        Loot.Clear();
        _deletedLootIds.Clear(); // tracker ids restart with the session
        _lootDirty = true;
        // The loot file is derived — with the rows gone this immediately
        // rewrites it marker-only, so a stale charge can't fire.
        SyncLootFile();
        _manager.RaidRoster.StartNewSession(DateTimeOffset.Now);
        Status = Loc.Get("Raid_SessionCleared");
        _dirty = true;
    }
}
