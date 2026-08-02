using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;
using Microsoft.Win32;

namespace EQ2Parser.App.ViewModels;

public sealed partial class SourceRow(LogSource source) : ObservableObject
{
    public LogSource Source { get; } = source;
    public string Owner => Source.Owner;
    public string Path => Source.Path;
    public string Mode => Source.AutoDiscovered ? "auto" : Source.ParseFromStart ? "full file" : "live tail";

    [ObservableProperty]
    private string _status = "";
}

/// <summary>Manage tailed log files. "Add live" starts at the end of the
/// file; "Add + parse existing" chews through the whole log first (useful
/// for history review of a past raid night).</summary>
public sealed partial class SourcesViewModel(SourceManager manager) : ObservableObject
{
    public ObservableCollection<SourceRow> Rows { get; } = [];
    public ObservableCollection<string> WatchedFolders { get; } = [.. manager.WatchedFolders];

    [RelayCommand]
    private void AddLive() => Add(parseFromStart: false);

    [RelayCommand]
    private void AddWithHistory() => Add(parseFromStart: true);

    [RelayCommand]
    private void WatchFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the EQ2 logs folder (server subfolders are scanned too)",
        };
        if (dialog.ShowDialog() != true)
            return;
        manager.AddWatchedFolder(dialog.FolderName);
        if (!WatchedFolders.Contains(dialog.FolderName))
            WatchedFolders.Add(dialog.FolderName);
    }

    [RelayCommand]
    private void UnwatchFolder(string? folder)
    {
        if (folder is null)
            return;
        manager.RemoveWatchedFolder(folder);
        WatchedFolders.Remove(folder);
        SyncFromManager();
    }

    [RelayCommand]
    private void Remove(SourceRow? row)
    {
        if (row is null)
            return;
        manager.Remove(row.Source);
        Rows.Remove(row);
        manager.PersistSources();
    }

    private void Add(bool parseFromStart)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an EQ2 log file",
            Filter = "EQ2 logs (eq2log_*.txt)|eq2log_*.txt|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;
        if (manager.Sources.Any(s => string.Equals(s.Path, dialog.FileName, StringComparison.OrdinalIgnoreCase)))
            return;
        var source = manager.Add(dialog.FileName, parseFromStart);
        Rows.Add(new SourceRow(source));
        manager.PersistSources();
    }

    /// <summary>Shell tick: pick up folder-watcher discoveries, then
    /// refresh per-source status lines. One lock for the whole snapshot
    /// (this used to take Sync once per source per 100ms tick), and the
    /// bound Status setters run after it is released.</summary>
    public void Refresh()
    {
        if (manager.Sources.Count != Rows.Count)
            SyncFromManager();
        var statuses = new string[Rows.Count];
        lock (manager.Sync)
        {
            for (var i = 0; i < Rows.Count; i++)
            {
                var row = Rows[i];
                statuses[i] = row.Source.Error is not null
                    ? $"error: {row.Source.Error.Message}"
                    : $"{row.Source.Processor.LinesSeen:N0} lines · {row.Source.Processor.LinesMatched:N0} matched · {row.Source.Engine.History.Count} encounters{(row.Source.Engine.InCombat ? " · IN COMBAT" : "")}";
            }
        }
        for (var i = 0; i < Rows.Count && i < statuses.Length; i++)
            Rows[i].Status = statuses[i];
    }

    public void SyncFromManager()
    {
        Rows.Clear();
        foreach (var source in manager.Sources)
            Rows.Add(new SourceRow(source));
    }
}
