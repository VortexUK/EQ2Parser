using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.History;

namespace EQ2Parser.App.ViewModels;

/// <summary>One archived fight in the search results.</summary>
public sealed partial class ArchiveRow(EncounterSummary summary, bool loaded) : ObservableObject
{
    public EncounterSummary Summary { get; } = summary;

    public string WhenLabel => Summary.Start.LocalDateTime.ToString("ddd d MMM · HH:mm");
    public string Zone => Summary.Zone;
    public string Title => Summary.Title;

    public string DetailLabel =>
        $"{TimeSpan.FromSeconds(Math.Max(0, Summary.DurationSeconds)):m\\:ss}  ·  " +
        $"{CombatantRow.Compact(Summary.Damage)} dmg  ·  {Summary.Owner}'s log" +
        (Summary.IsBoss ? "" : "  ·  trash");

    public Brush OutcomeBrush => Summary.Success switch
    {
        SuccessLevel.Win => ClassColors.OutcomeWin,
        SuccessLevel.Loss => ClassColors.OutcomeLoss,
        _ => ClassColors.OutcomePartial,
    };

    [ObservableProperty]
    private bool _isLoaded = loaded;
}

/// <summary>
/// The parse archive: search everything ever stored (bosses never age
/// out), pull fights back into the parser, or purge them for good.
/// </summary>
public sealed partial class ArchiveViewModel : ObservableObject
{
    private readonly SourceManager _manager;

    public ObservableCollection<ArchiveRow> Rows { get; } = [];

    public IReadOnlyList<string> RangeChoices { get; } =
        ["Last 7 days", "Last 30 days", "Last 3 months", "Last year", "Everything"];

    private static readonly int?[] RangeDays = [7, 30, 90, 365, null];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _rangeIndex = 2;

    [ObservableProperty]
    private string _resultLabel = "";

    public ArchiveViewModel(SourceManager manager)
    {
        _manager = manager;
        Search();
    }

    partial void OnRangeIndexChanged(int value) => Search();

    [RelayCommand]
    private void Search()
    {
        var days = RangeDays[Math.Clamp(RangeIndex, 0, RangeDays.Length - 1)];
        var since = days is { } d ? DateTimeOffset.Now.AddDays(-d) : (DateTimeOffset?)null;
        var text = SearchText.Trim();
        var results = _manager.History.Search(text.Length == 0 ? null : text, since);
        Rows.Clear();
        foreach (var summary in results)
            Rows.Add(new ArchiveRow(summary, _manager.History.IsLoaded(summary.Id)));
        var bosses = results.Count(s => s.IsBoss);
        ResultLabel = results.Count == 0
            ? "Nothing found."
            : $"{results.Count} fight{(results.Count == 1 ? "" : "s")} · {bosses} boss{(bosses == 1 ? "" : "es")}";
    }

    [RelayCommand]
    private void LoadRow(ArchiveRow? row)
    {
        if (row is null || row.IsLoaded)
            return;
        lock (_manager.Sync)
        {
            _manager.History.LoadFight(row.Summary, _manager.Correlator);
        }
        row.IsLoaded = true;
    }

    [RelayCommand]
    private void LoadAll()
    {
        foreach (var row in Rows)
            LoadRow(row);
    }

    [RelayCommand]
    private void PurgeRow(ArchiveRow? row)
    {
        if (row is null)
            return;
        _manager.History.PurgeFromArchive(row.Summary.Id);
        Rows.Remove(row);
    }
}
