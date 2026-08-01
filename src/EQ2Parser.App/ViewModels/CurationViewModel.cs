using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.ViewModels;

/// <summary>Mob header row in the curation list.</summary>
public sealed record CurationMobRow(string Mob, int AbilityCount);

/// <summary>One mined ability row with its measured stats.</summary>
public sealed partial class CurationAbilityRow(MinedAbility mined) : ObservableObject
{
    public MinedAbility Mined { get; } = mined;
    public string Ability => Mined.Ability;

    public string CastsLabel =>
        $"{Mined.Casts} cast{(Mined.Casts == 1 ? "" : "s")} / {Mined.Fights} fight{(Mined.Fights == 1 ? "" : "s")}";

    /// <summary>Swipe-adjusted base recast leads when recast debuffs were
    /// in play; the raw observed median stays visible beside it.</summary>
    public string RecastLabel
    {
        get
        {
            if (Mined.MedianIntervalSeconds is not { } median)
                return "single cast";
            if (Mined.SwipeCoverage > 0.02 && Mined.BaseIntervalSeconds is { } baseline)
                return $"~{baseline:0}s base  (raw {median:0}s · min {Mined.MinIntervalSeconds:0}s)";
            return $"~{median:0}s  (min {Mined.MinIntervalSeconds:0}s)";
        }
    }

    public string RecastToolTip =>
        Mined.SwipeCoverage > 0.02
            ? $"Swipe-adjusted: {Mined.SwipeCoverage:P0} of interval time was under a recast debuff " +
              $"(counted at 1/1.5 rate). Base ≈ {Mined.BaseIntervalSeconds:0.#}s — the right duration to store, " +
              "because the live timer re-stretches automatically when the mob gets swiped."
            : "No recast debuffs observed on this mob — raw and base agree.";

    public string ProfileLabel
    {
        get
        {
            var perCast = Mined.Casts > 0 ? Mined.TotalDamage / Mined.Casts : 0;
            var kind = Mined.IsMelee ? "melee" : "spell";
            var targets = Mined.AvgTargets >= 1.8 ? $" · AOE ×{Mined.AvgTargets:0.#}" : "";
            return $"{CombatantRow.Compact(perCast)}/cast · {kind}{targets}";
        }
    }

    /// <summary>A local spell timer with this name already exists — after
    /// re-importing the old ACT pack this doubles as the "in ACT" column.</summary>
    [ObservableProperty]
    private bool _hasTimer;

    public bool CanTime => Mined.MedianIntervalSeconds is not null;
}

/// <summary>
/// The curation workbench: mine archived fights for what each mob actually
/// casts (measured recast intervals = ground-truth timer durations), see at
/// a glance which abilities already have local timers, and mint the missing
/// ones — locally or as ACT XML for the Lexicon curator pages.
/// </summary>
public sealed partial class CurationViewModel : ObservableObject
{
    private readonly SourceManager _manager;

    public ObservableCollection<string> Zones { get; } = [];
    public ObservableCollection<object> Rows { get; } = [];

    [ObservableProperty]
    private string? _selectedZone;

    [ObservableProperty]
    private string _statusLabel = "";

    public CurationViewModel(SourceManager manager)
    {
        _manager = manager;
        foreach (var zone in manager.History.ArchivedZones())
            Zones.Add(zone);
        StatusLabel = Zones.Count == 0
            ? "No archived boss fights yet — raid a little, then come back."
            : "Pick a zone to mine its archived fights.";
    }

    partial void OnSelectedZoneChanged(string? value)
    {
        if (value is not null)
            Mine(value);
    }

    private void Mine(string zone)
    {
        Rows.Clear();
        var mobs = AbilityMiner.MineZone(_manager.History, zone);
        var abilities = 0;
        var missing = 0;
        foreach (var mob in mobs)
        {
            Rows.Add(new CurationMobRow(mob.Mob, mob.Abilities.Count));
            foreach (var mined in mob.Abilities)
            {
                var row = new CurationAbilityRow(mined) { HasTimer = TimerExists(mined.Ability) };
                Rows.Add(row);
                abilities++;
                if (!row.HasTimer && row.CanTime)
                    missing++;
            }
        }
        StatusLabel = mobs.Count == 0
            ? "No boss fights archived for this zone."
            : $"{mobs.Count} mob{(mobs.Count == 1 ? "" : "s")} · {abilities} abilit{(abilities == 1 ? "y" : "ies")} · {missing} timeable without a timer";
    }

    private bool TimerExists(string ability) =>
        _manager.SpellTimers.Definitions.Any(d =>
            string.Equals(d.Name, ability, StringComparison.OrdinalIgnoreCase));

    private TimerDefinition BuildDefinition(MinedAbility mined)
    {
        // Base (swipe-adjusted) duration: the live engine re-applies the
        // 1.5× stretch whenever the mob is actually swiped.
        var duration = (int)Math.Round(mined.BaseIntervalSeconds ?? mined.MedianIntervalSeconds ?? 30);
        return new TimerDefinition
        {
            Name = mined.Ability,
            Category = mined.Zone,
            DurationSeconds = Math.Max(5, duration),
            WarningSeconds = Math.Clamp(duration / 4, 3, 10),
        };
    }

    [RelayCommand]
    private void CreateTimer(CurationAbilityRow? row)
    {
        if (row is null || row.HasTimer || !row.CanTime)
            return;
        var definition = BuildDefinition(row.Mined);
        _manager.SpellTimers.AddOrUpdate(definition);
        row.HasTimer = true;
        StatusLabel = $"Timer created: {row.Ability} ({definition.DurationSeconds}s base) — it's on the Timers page.";
    }

    [RelayCommand]
    private void CopyXml(CurationAbilityRow? row)
    {
        if (row is null)
            return;
        Clipboard.SetText(ActShareFormat.Export(BuildDefinition(row.Mined)));
        StatusLabel = $"<Spell> XML for {row.Ability} copied — paste into the Lexicon encounter's ACT panel.";
    }

    /// <summary>Every timeable, timer-less ability in the zone as one XML
    /// block — the bulk hand-off to Lexicon curation.</summary>
    [RelayCommand]
    private void CopyMissingXml()
    {
        List<string> lines = [];
        foreach (var item in Rows)
        {
            if (item is CurationAbilityRow { HasTimer: false, CanTime: true } row)
                lines.Add(ActShareFormat.Export(BuildDefinition(row.Mined)));
        }
        if (lines.Count == 0)
        {
            StatusLabel = "Nothing missing — every timeable ability already has a timer.";
            return;
        }
        Clipboard.SetText(string.Join(Environment.NewLine, lines));
        StatusLabel = $"{lines.Count} missing timer{(lines.Count == 1 ? "" : "s")} copied as XML.";
    }
}
