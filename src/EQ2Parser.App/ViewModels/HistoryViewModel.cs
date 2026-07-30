using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;

namespace EQ2Parser.App.ViewModels;

public sealed partial class EncounterRow : ObservableObject
{
    public required CorrelatedEncounter Encounter { get; init; }
    public required string When { get; init; }
    public required string Zone { get; init; }
    public required string Title { get; init; }
    public required string Duration { get; init; }
    public required string Dps { get; init; }
    public required string Outcome { get; init; }
    public required string SourceCount { get; init; }
}

/// <summary>Correlated fight history — newest first; selecting a row shows
/// its (merged) combatant breakdown using the same meter row template.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly SourceManager _manager;

    public ObservableCollection<EncounterRow> Encounters { get; } = [];
    public ObservableCollection<CombatantRow> Rows { get; } = [];

    [ObservableProperty]
    private EncounterRow? _selected;

    public HistoryViewModel(SourceManager manager)
    {
        _manager = manager;
        manager.HistoryChanged += () =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(Resync);
        Resync();
    }

    partial void OnSelectedChanged(EncounterRow? value)
    {
        Rows.Clear();
        if (value is null)
            return;

        List<(string Key, string Name, long Damage, double Dps, string Cls, bool IsPet)> snapshot = [];
        lock (_manager.Sync)
        {
            var encounter = value.Encounter;
            var tags = _manager.Classifier.Classify(encounter.Primary);
            var seconds = Math.Max(1, encounter.Duration.TotalSeconds);
            foreach (var (key, merged) in encounter.MergedCombatants)
            {
                if (!encounter.MergedAllyKeys.Contains(key))
                    continue;
                var combatant = merged.Combatant;
                if (combatant.Damage <= 0 && combatant.Healed <= 0)
                    continue;
                var cls = "";
                var isPet = false;
                if (tags.TryGetValue(key, out var tag))
                {
                    if (tag.Kind is CombatantKind.System or CombatantKind.Bystander)
                        continue;
                    isPet = tag.Kind == CombatantKind.Pet;
                    cls = isPet
                        ? (tag.PetOwner is not null ? $"pet · {tag.PetOwner}" : "pet")
                        : tag.Class.ClassName ?? "";
                }
                snapshot.Add((key, combatant.Name, combatant.Damage, combatant.Damage / seconds, cls, isPet));
            }
        }

        snapshot.Sort((a, b) => b.Damage.CompareTo(a.Damage));
        var top = snapshot.Count > 0 ? Math.Max(1, snapshot[0].Damage) : 1;
        var total = Math.Max(1, snapshot.Sum(r => r.Damage));
        foreach (var (key, name, damage, dps, cls, isPet) in snapshot)
        {
            var row = new CombatantRow { Key = key };
            row.Name = name;
            row.ClassName = cls;
            row.IsPet = isPet;
            row.Dps = CombatantRow.Compact(dps);
            row.Damage = CombatantRow.Compact(damage);
            row.Percent = $"{100.0 * damage / total:F0}%";
            row.BarFraction = (double)damage / top;
            Rows.Add(row);
        }
    }

    public void Resync()
    {
        List<EncounterRow> rows = [];
        lock (_manager.Sync)
        {
            foreach (var encounter in _manager.Correlator.History.Reverse())
            {
                rows.Add(new EncounterRow
                {
                    Encounter = encounter,
                    When = encounter.StartTime.ToLocalTime().ToString("ddd HH:mm"),
                    Zone = encounter.Zone,
                    Title = encounter.Title,
                    Duration = $"{encounter.Duration.TotalSeconds:F0}s",
                    Dps = CombatantRow.Compact(encounter.EncDps),
                    Outcome = encounter.GetSuccessLevel() switch
                    {
                        SuccessLevel.Win => "Win",
                        SuccessLevel.Loss => "Loss",
                        SuccessLevel.Partial => "Partial",
                        _ => "—",
                    },
                    SourceCount = encounter.Sources.Count > 1 ? $"{encounter.Sources.Count} logs" : "",
                });
            }
        }

        Encounters.Clear();
        foreach (var row in rows)
            Encounters.Add(row);
    }
}
