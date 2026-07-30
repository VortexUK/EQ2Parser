using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;

namespace EQ2Parser.App.ViewModels;

/// <summary>
/// The live meter: shows the active encounter (any source in combat), or the
/// most recently ended one. Refreshed by the shell's ~100ms coalescing tick —
/// snapshots engine state under the manager lock, then updates rows in place.
/// </summary>
public sealed partial class LiveViewModel(SourceManager manager) : ObservableObject
{
    [ObservableProperty]
    private string _title = "No encounter yet";

    [ObservableProperty]
    private string _subtitle = "Add a log source to begin.";

    [ObservableProperty]
    private bool _inCombat;

    public ObservableCollection<CombatantRow> Rows { get; } = [];

    [RelayCommand]
    private void EndCombat()
    {
        lock (manager.Sync)
        {
            foreach (var source in manager.Sources)
                source.Engine.EndCombat();
        }
        Refresh();
    }

    /// <summary>Called on the UI thread by the shell tick.</summary>
    public void Refresh()
    {
        Encounter? encounter = null;
        var active = false;
        IReadOnlyDictionary<string, CombatantTag>? tags = null;
        List<(string Key, string Name, long Damage, double Dps, string? Owner, CombatantKind Kind, string Cls)> snapshot = [];

        lock (manager.Sync)
        {
            foreach (var source in manager.Sources)
            {
                if (source.Engine.ActiveEncounter is { } current)
                {
                    encounter = current;
                    active = true;
                    break;
                }
                var last = source.Engine.History.Count > 0 ? source.Engine.History[^1] : null;
                if (last is not null && (encounter is null || last.EndTime > encounter.EndTime))
                    encounter = last;
            }

            if (encounter is not null)
            {
                tags = manager.Classifier.Classify(encounter);
                foreach (var ally in encounter.GetAllies())
                {
                    var tag = tags[ally.Key];
                    if (tag.Kind is CombatantKind.System or CombatantKind.Bystander)
                        continue;
                    if (ally.Damage <= 0 && ally.Healed <= 0)
                        continue;
                    snapshot.Add((
                        ally.Key, ally.Name, ally.Damage, encounter.EncDpsOf(ally),
                        tag.PetOwner, tag.Kind, tag.Class.ClassName ?? ""));
                }
                Title = encounter.Title == Encounter.PlaceholderTitle && active
                    ? "Combat…"
                    : encounter.Title;
                Subtitle = $"{encounter.Zone}  ·  {encounter.Duration.TotalSeconds:F0}s  ·  raid {CombatantRow.Compact(encounter.EncDps)} dps";
                InCombat = active;
            }
        }

        if (encounter is null)
            return;

        snapshot.Sort((a, b) => b.Damage.CompareTo(a.Damage));
        var top = snapshot.Count > 0 ? Math.Max(1, snapshot[0].Damage) : 1;
        var totalDamage = Math.Max(1, snapshot.Sum(r => r.Damage));

        // Update rows in place; trim/extend to match.
        for (var i = 0; i < snapshot.Count; i++)
        {
            var (key, name, damage, dps, owner, kind, cls) = snapshot[i];
            CombatantRow row;
            if (i < Rows.Count)
            {
                row = Rows[i];
            }
            else
            {
                row = new CombatantRow { Key = key };
                Rows.Add(row);
            }
            row.Name = owner is not null ? $"{name}" : name;
            row.ClassName = kind == CombatantKind.Pet ? (owner is not null ? $"pet · {owner}" : "pet") : cls;
            row.Kind = kind;
            row.IsPet = kind == CombatantKind.Pet;
            row.Dps = CombatantRow.Compact(dps);
            row.Damage = CombatantRow.Compact(damage);
            row.Percent = $"{100.0 * damage / totalDamage:F0}%";
            row.BarFraction = (double)damage / top;
        }
        while (Rows.Count > snapshot.Count)
            Rows.RemoveAt(Rows.Count - 1);
    }
}
