using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;

namespace EQ2Parser.App.ViewModels;

/// <summary>Same-boss fight comparison: pick an earlier kill of the same
/// mob (same title + zone) from the context menu and get a report of
/// per-player DPS/HPS deltas plus raid totals — "did tonight beat last
/// week". Report text stays English like every other report.</summary>
public sealed partial class MainParseViewModel
{
    /// <summary>Earlier/other loaded fights comparable to
    /// <paramref name="fight"/>: same title AND zone (the same boss name
    /// can exist in two zones with different tuning), the SAME OUTCOME
    /// (a kill only compares against kills, a wipe against wipes — a 7s
    /// faceplant against a 6-minute kill is noise), and at least a
    /// quarter of the fight's duration (drops instant resets even when
    /// the outcome heuristic reads them the same). Newest first. The
    /// archive isn't searched — pull an old kill back into the parser
    /// from the Archive window first and it appears here.</summary>
    public IReadOnlyList<CorrelatedEncounter> ComparableFights(CorrelatedEncounter fight)
    {
        lock (manager.Sync)
        {
            var outcome = fight.GetSuccessLevel();
            var floor = TimeSpan.FromTicks(fight.Duration.Ticks / 4);
            return [.. manager.Correlator.History
                .Where(f => !ReferenceEquals(f, fight)
                    && string.Equals(f.Title, fight.Title, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.Zone, fight.Zone, StringComparison.OrdinalIgnoreCase)
                    && SameOutcome(f.GetSuccessLevel(), outcome)
                    && f.Duration >= floor)
                .OrderByDescending(f => f.StartTime)
                .Take(10)];
        }
    }

    /// <summary>Win only pairs with Win, Loss with Loss; Indeterminate and
    /// Partial pair with anything (old logs and messy pulls shouldn't
    /// empty the picker).</summary>
    private static bool SameOutcome(SuccessLevel a, SuccessLevel b) =>
        a is SuccessLevel.Indeterminate or SuccessLevel.Partial
        || b is SuccessLevel.Indeterminate or SuccessLevel.Partial
        || a == b;

    /// <summary>Menu label for one comparable fight — date, duration, raid DPS.</summary>
    public static string CompareLabel(CorrelatedEncounter fight) =>
        $"{fight.StartTime.LocalDateTime:ddd d MMM HH:mm} · {FmtSpan(fight.Duration)} · {CombatantRow.Compact(fight.EncDps)}";

    /// <summary>Per-player metrics for one side of the comparison.</summary>
    private sealed record CompareSide(double Dps, double Hps, int Deaths, string Cls);

    public void CompareFights(CorrelatedEncounter current, CorrelatedEncounter baseline)
    {
        lock (manager.Sync)
        {
            var a = SideOf(current);
            var b = SideOf(baseline);

            ReportLine(($"COMPARISON — {current.Title} ({current.Zone})", Services.ClassColors.TreeHeader));
            ReportLine(
                ("A  ", Services.ClassColors.OutcomeWin),
                ($"{current.StartTime.LocalDateTime:ddd d MMM HH:mm} · {FmtSpan(current.Duration)} · raid {CombatantRow.Compact(current.EncDps)} dps", Services.ClassColors.TreeText));
            ReportLine(
                ("B  ", Services.ClassColors.Neutral),
                ($"{baseline.StartTime.LocalDateTime:ddd d MMM HH:mm} · {FmtSpan(baseline.Duration)} · raid {CombatantRow.Compact(baseline.EncDps)} dps", Services.ClassColors.TreeText));

            var raidDelta = baseline.EncDps > 0 ? (current.EncDps - baseline.EncDps) / baseline.EncDps : 0;
            var durDelta = current.Duration - baseline.Duration;
            ReportLine(
                ("raid ", Services.ClassColors.Neutral),
                ($"{Signed(current.EncDps - baseline.EncDps)} dps ({raidDelta:+0.0%;-0.0%})", DeltaBrush(raidDelta)),
                ("   duration ", Services.ClassColors.Neutral),
                ($"{(durDelta < TimeSpan.Zero ? "-" : "+")}{FmtSpan(durDelta.Duration())}", DeltaBrush(-durDelta.TotalSeconds)));
            ReportLine(("", Services.ClassColors.Neutral));

            ReportLine(
                ("NAME".PadRight(18), Services.ClassColors.TreeHeader),
                ("CLASS".PadRight(14), Services.ClassColors.TreeHeader),
                ("   A DPS    B DPS     ΔDPS      Δ%", Services.ClassColors.TreeHeader),
                ("     A HPS    B HPS   DEATHS", Services.ClassColors.TreeHeader));

            foreach (var name in a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(n => a.GetValueOrDefault(n)?.Dps ?? b.GetValueOrDefault(n)?.Dps ?? 0))
            {
                var inA = a.GetValueOrDefault(name);
                var inB = b.GetValueOrDefault(name);
                var cls = inA?.Cls ?? inB?.Cls ?? "";
                var delta = inA is not null && inB is not null && inB.Dps > 0
                    ? (inA.Dps - inB.Dps) / inB.Dps : (double?)null;
                ReportLine(
                    (name.PadRight(18), Services.ClassColors.For(cls)),
                    (cls.PadRight(14), Services.ClassColors.For(cls)),
                    ($"{Cell(inA?.Dps)} {Cell(inB?.Dps)} {Cell(inA is null || inB is null ? null : inA.Dps - inB.Dps, signed: true)}", Services.ClassColors.TreeText),
                    (delta is null ? "        —" : $"{delta:+0.0%;-0.0%}".PadLeft(9), delta is null ? Services.ClassColors.Neutral : DeltaBrush(delta.Value)),
                    ($"  {Cell(inA?.Hps)} {Cell(inB?.Hps)}", Services.ClassColors.TreeText),
                    ($"   {inA?.Deaths.ToString() ?? "—"}→{inB?.Deaths.ToString() ?? "—"}", Services.ClassColors.Neutral),
                    (inA is null ? "   (absent in A)" : inB is null ? "   (absent in B)" : "", Services.ClassColors.Neutral));
            }
        }
        OpenReport($"{current.Title} › comparison");
    }

    private static string Signed(double v) => v >= 0 ? $"+{CombatantRow.Compact(v)}" : $"-{CombatantRow.Compact(-v)}";

    private static string Cell(double? v, bool signed = false) =>
        (v is null ? "—" : signed ? Signed(v.Value) : CombatantRow.Compact(v.Value)).PadLeft(8);

    /// <summary>Green when the delta helps (more dps / shorter fight),
    /// red when it hurts, neutral for tiny moves under ±2%.</summary>
    private static System.Windows.Media.Brush DeltaBrush(double delta) => delta switch
    {
        > 0.02 => Services.ClassColors.OutcomeWin,
        < -0.02 => Services.ClassColors.OutcomeLoss,
        _ => Services.ClassColors.Neutral,
    };

    private Dictionary<string, CompareSide> SideOf(CorrelatedEncounter fight)
    {
        var tags = manager.Classifier.Classify(fight.Primary);
        var seconds = Math.Max(1, fight.Duration.TotalSeconds);
        Dictionary<string, CompareSide> side = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, entry) in fight.MergedCombatants)
        {
            if (!fight.MergedAllyKeys.Contains(key)
                || !tags.TryGetValue(key, out var tag) || tag.Kind != CombatantKind.Player)
                continue;
            var combatant = entry.Combatant;
            if (combatant.Damage <= 0 && combatant.Healed <= 0)
                continue;
            side[combatant.Name] = new CompareSide(
                combatant.Damage / seconds, combatant.Healed / seconds,
                combatant.Deaths, tag.Class.ClassName ?? "");
        }
        return side;
    }
}
