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
    public string Mode => Source.ParseFromStart ? "full file" : "live tail";

    [ObservableProperty]
    private string _status = "";
}

/// <summary>Manage tailed log files. "Add live" starts at the end of the
/// file; "Add + parse existing" chews through the whole log first (useful
/// for history review of a past raid night).</summary>
public sealed partial class SourcesViewModel(SourceManager manager) : ObservableObject
{
    public ObservableCollection<SourceRow> Rows { get; } = [];

    [RelayCommand]
    private void AddLive() => Add(parseFromStart: false);

    [RelayCommand]
    private void AddWithHistory() => Add(parseFromStart: true);

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

    /// <summary>Shell tick: refresh per-source status lines.</summary>
    public void Refresh()
    {
        foreach (var row in Rows)
        {
            long seen, matched;
            int encounters;
            bool inCombat;
            lock (manager.Sync)
            {
                seen = row.Source.Processor.LinesSeen;
                matched = row.Source.Processor.LinesMatched;
                encounters = row.Source.Engine.History.Count;
                inCombat = row.Source.Engine.InCombat;
            }
            row.Status = row.Source.Error is not null
                ? $"error: {row.Source.Error.Message}"
                : $"{seen:N0} lines · {matched:N0} matched · {encounters} encounters{(inCombat ? " · IN COMBAT" : "")}";
        }
    }

    public void SyncFromManager()
    {
        Rows.Clear();
        foreach (var source in manager.Sources)
            Rows.Add(new SourceRow(source));
    }
}
