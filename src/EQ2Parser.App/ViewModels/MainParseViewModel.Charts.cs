using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace EQ2Parser.App.ViewModels;

/// <summary>Encounter-summary chart + the depth-aware drill charts
/// (bucket activity lines, ability doughnut, per-skill heat strip).</summary>
public sealed partial class MainParseViewModel
{
    // ── Chart snapshot ──────────────────────────────────────────────────────

    private sealed record ChartData(string MetricLabel, bool IsRollup, List<(string Label, double Value, SKColor Color)> Columns);

    private static readonly SKColor ChartGold = new(0xC8, 0xA9, 0x6E, 0xB0);

    private string ChartMetric => SortColumn switch
    {
        "Hps" => "HPS",
        "Taken" => "Taken",
        _ => "DPS",
    };

    private static double MetricOf(RowData row, string metric) => metric switch
    {
        "HPS" => row.Hps,
        "Taken" => row.Taken,
        _ => row.Dps,
    };

    /// <summary>Encounter-summary chart: one column per ally (archetype
    /// colours) for single fights, one gold column per fight for rollups.
    /// Rebuilds when the fight/metric changes or (throttled ~1s) on new
    /// live data.</summary>
    private ChartData? MaybeSnapshotChart(object fight, List<RowData> allies)
    {
        var metric = ChartMetric;
        var version = allies.Sum(r => r.Damage + r.Taken) + (long)allies.Sum(r => r.Hps * 100);
        var keyChanged = !ReferenceEquals(_chartKey.Fight, fight) || _chartKey.Metric != metric;
        var now = Environment.TickCount64;
        if (!keyChanged && (version == _chartVersion || now - _lastChartBuildMs < 1000))
            return null;
        if (keyChanged)
            _chartOrder = null;
        _chartKey = (fight, metric);
        _chartVersion = version;
        _lastChartBuildMs = now;

        List<(string, double, SKColor)> columns = [];
        if (fight is AggregateFights aggregate)
        {
            foreach (var f in aggregate.Fights)
            {
                var seconds = ((IFightView)f).DisplaySeconds;
                double total = 0;
                foreach (var (_, combatant) in ((IFightView)f).AllyCombatants)
                {
                    total += metric switch
                    {
                        "HPS" => combatant.Healed,
                        "Taken" => combatant.DamageTaken,
                        _ => combatant.Damage,
                    };
                }
                var title = f.Title.Length > 16 ? f.Title[..15] + "…" : f.Title;
                columns.Add((title, metric == "Taken" ? total : total / seconds, ChartGold));
            }
            return new ChartData(metric, IsRollup: true, columns);
        }

        foreach (var row in allies.OrderByDescending(r => MetricOf(r, metric)))
        {
            var value = MetricOf(row, metric);
            if (value <= 0)
                continue;
            var media = ((System.Windows.Media.SolidColorBrush)row.Brush).Color;
            columns.Add((row.Name, value, new SKColor(media.R, media.G, media.B, 0xC8)));
        }
        // Rank hysteresis: a reorder is a structural change (full rebuild,
        // bars re-enter), and close DPSers trade places every tick on live
        // — hold the previous order for a few seconds so the chart tweens
        // values at 1 Hz and re-sorts at most every 5 s.
        if (_chartOrder is { } held && held.Count == columns.Count
            && now - _lastChartReorderMs < 5000
            && columns.All(c => held.Contains(c.Item1)))
        {
            columns = [.. columns.OrderBy(c => held.IndexOf(c.Item1))];
        }
        else
        {
            _chartOrder = [.. columns.Select(c => c.Item1)];
            _lastChartReorderMs = now;
        }
        return new ChartData(metric, IsRollup: false, columns);
    }

    private List<string>? _chartOrder;
    private long _lastChartReorderMs;

    // LiveCharts paints carry per-canvas state — every axis/legend needs its
    // own instance, never a shared static (sharing silently drops labels).
    private static SolidColorPaint MutedPaint() => new(new SKColor(0x8B, 0x90, 0xAB));
    private static SolidColorPaint SeparatorPaint() => new(new SKColor(0x2E, 0x31, 0x50, 0x90));

    // Live-refresh stability: the series/axis objects survive between
    // refreshes and only their VALUES change, so columns tween smoothly to
    // new heights. Replacing ChartSeries wholesale every second made
    // LiveCharts tear down and re-enter every bar from zero — the
    // "epileptic" live chart. A structural change (bars added/removed,
    // rank order shift, metric change) still rebuilds.
    private string? _chartSignature;
    private readonly Dictionary<SKColor, ColumnSeries<double?>> _chartSeriesByColor = [];
    private LineSeries<double>? _chartAverageSeries;

    private void ApplyChart(ChartData chart)
    {
        var signature = $"{chart.MetricLabel}\u0001{chart.IsRollup}\u0001"
            + string.Join("\u0001", chart.Columns.Select(c => $"{c.Label}#{(uint)c.Color}"));
        if (signature == _chartSignature && ChartSeries.Length > 0)
        {
            MutateChartValues(chart);
            return;
        }
        _chartSignature = signature;
        RebuildChart(chart);
    }

    /// <summary>Same bars, same order, same metric — pour the new numbers
    /// into the existing series so the chart animates deltas.</summary>
    private void MutateChartValues(ChartData chart)
    {
        var count = chart.Columns.Count;
        foreach (var (color, series) in _chartSeriesByColor)
        {
            var values = new double?[count];
            for (var i = 0; i < count; i++)
            {
                if (chart.Columns[i].Color == color)
                    values[i] = Math.Round(chart.Columns[i].Value);
            }
            series.Values = values;
        }
        if (_chartAverageSeries is not null && count > 1)
        {
            var average = Math.Round(chart.Columns.Average(c => c.Value));
            _chartAverageSeries.Values = [.. Enumerable.Repeat(average, count)];
        }
    }

    private void RebuildChart(ChartData chart)
    {
        // Per-column colours: one full-width series per distinct colour with
        // nulls elsewhere (IgnoresBarPosition keeps every bar centred), plus
        // a dashed gold average line across the set.
        var count = chart.Columns.Count;
        _chartSeriesByColor.Clear();
        _chartAverageSeries = null;
        List<ISeries> series = [.. chart.Columns
            .Select((c, i) => (c, i))
            .GroupBy(t => t.c.Color)
            .Select(ISeries (group) =>
            {
                var values = new double?[count];
                foreach (var (c, i) in group)
                    values[i] = Math.Round(c.Value);
                var columnSeries = new ColumnSeries<double?>
                {
                    Values = values,
                    Name = chart.MetricLabel,
                    Fill = new SolidColorPaint(group.Key),
                    IgnoresBarPosition = true,
                    MaxBarWidth = 34,
                    Rx = 3,
                    Ry = 3,
                };
                _chartSeriesByColor[group.Key] = columnSeries;
                return columnSeries;
            })];
        if (count > 1)
        {
            var average = Math.Round(chart.Columns.Average(c => c.Value));
            series.Add(_chartAverageSeries = new LineSeries<double>
            {
                Values = [.. Enumerable.Repeat(average, count)],
                Name = "average",
                Stroke = new SolidColorPaint(new SKColor(0xE8, 0xD5, 0xA3, 0xC0))
                {
                    StrokeThickness = 1.6f,
                    PathEffect = new LiveChartsCore.SkiaSharpView.Painting.Effects.DashEffect([6f, 5f]),
                },
                Fill = null,
                GeometrySize = 0,
                GeometryStroke = null,
                GeometryFill = null,
                LineSmoothness = 0,
            });
        }
        ChartSeries = [.. series];
        ChartXAxes =
        [
            new Axis
            {
                Labels = chart.Columns.Select(c => c.Label).ToArray(),
                LabelsPaint = MutedPaint(),
                LabelsRotation = -35,
                TextSize = 10.5,
                SeparatorsPaint = null,
            },
        ];
        ChartYAxes =
        [
            new Axis
            {
                Name = chart.IsRollup ? $"raid {chart.MetricLabel}" : chart.MetricLabel,
                NamePaint = MutedPaint(),
                NameTextSize = 11,
                LabelsPaint = MutedPaint(),
                TextSize = 11,
                Labeler = v => CombatantRow.Compact(v),
                SeparatorsPaint = SeparatorPaint(),
                MinLimit = 0,
            },
        ];
        ChartVisible = ChartSeries.Length > 0;
    }

    // ── Drill chart data + rendering ────────────────────────────────────────

    /// <summary>Mode 0 = hidden, 1 = bucket activity lines over time,
    /// 2 = ability doughnut, 3 = heat strip over time.</summary>
    private sealed record DrillChart(
        int Mode,
        List<(string Name, SKColor Color, double[] Rates)>? Lines,
        double BucketSeconds,
        List<(string Label, double Value)>? Slices,
        double[]? Heat);

    private static readonly (string Bucket, SKColor Color)[] DrillLineBuckets =
    [
        (BucketConfig.AutoAttackOut, new SKColor(0xC8, 0xA9, 0x6E)),
        (BucketConfig.SkillOut, new SKColor(0x93, 0xB4, 0xFF)),
        (BucketConfig.HealedOut, new SKColor(0x4A, 0xDE, 0x80)),
        (BucketConfig.PowerDrainOut, new SKColor(0x00, 0xE5, 0xFF)),
        (BucketConfig.PowerReplenishOut, new SKColor(0x22, 0xD3, 0xEE)),
        (BucketConfig.CureOut, new SKColor(0xE8, 0xBB, 0xFF)),
        (BucketConfig.ThreatOut, new SKColor(0xF8, 0x71, 0x71)),
    ];

    private static (DateTimeOffset Start, double BucketSeconds, int Slots)? FightWindow(object fight)
    {
        DateTimeOffset start, end;
        switch (fight)
        {
            case Encounter encounter:
                start = encounter.StartTime;
                end = encounter.EndTime;
                break;
            case CorrelatedEncounter merged:
                start = merged.StartTime;
                end = merged.EndTime;
                break;
            default:
                return null;
        }
        var duration = Math.Max(1, (end - start).TotalSeconds);
        var bucketSeconds = Math.Clamp(Math.Ceiling(duration / 60), 2, 30);
        return (start, bucketSeconds, (int)(duration / bucketSeconds) + 1);
    }

    /// <summary>Throttle: rebuild on drill-path change; otherwise only when
    /// the underlying data grew (live fight), at most ~1s. Ended fights
    /// build once and stay still.</summary>
    private bool ShouldBuildDrillChart(long version)
    {
        var key = (_detailKey, _detailBucket, _detailAbility);
        var now = Environment.TickCount64;
        if (key != _drillChartKey)
        {
            _drillChartKey = key;
            _drillChartVersion = version;
            _drillChartMs = now;
            return true;
        }
        if (version == _drillChartVersion || now - _drillChartMs < 1000)
            return false;
        _drillChartVersion = version;
        _drillChartMs = now;
        return true;
    }

    private static readonly DrillChart HiddenDrillChart = new(0, null, 0, null, null);

    private static void AccumulateRates(double[] rates, IEnumerable<Core.Combat.Swing> swings, DateTimeOffset start, double bucketSeconds)
    {
        foreach (var swing in swings)
        {
            if (swing.Damage.Number <= 0)
                continue;
            var slot = (int)((swing.Time - start).TotalSeconds / bucketSeconds);
            if (slot >= 0 && slot < rates.Length)
                rates[slot] += swing.Damage.Number;
        }
    }

    private void ApplyDrillChart(DrillChart chart)
    {
        switch (chart.Mode)
        {
            case 1 when chart.Lines is { } lines:
                {
                    var bucket = chart.BucketSeconds;
                    DrillCartesianSeries = [.. lines.Select(ISeries (line) => new LineSeries<double>
                {
                    Values = line.Rates,
                    Name = line.Name,
                    Stroke = new SolidColorPaint(line.Color) { StrokeThickness = 2 },
                    Fill = null,
                    GeometrySize = 0,
                    GeometryStroke = null,
                    GeometryFill = null,
                    LineSmoothness = 0.4,
                })];
                    DrillXAxes = [TimeAxis(bucket)];
                    DrillYAxes =
                    [
                        new Axis
                    {
                        LabelsPaint = MutedPaint(),
                        TextSize = 11,
                        Labeler = v => CombatantRow.Compact(v),
                        SeparatorsPaint = SeparatorPaint(),
                        MinLimit = 0,
                    },
                ];
                    DrillCartesianVisible = true;
                    DrillDonutVisible = false;
                    break;
                }
            case 2 when chart.Slices is { } slices:
                {
                    var total = Math.Max(1, slices.Sum(s => s.Value));
                    DrillDonutSeries = [.. slices.Select(ISeries (slice, i) =>
                {
                    var share = slice.Value / total;
                    return new PieSeries<double>
                    {
                        Values = new[] { Math.Round(slice.Value) },
                        Name = $"{share * 100:F0}%  {slice.Label}",
                        Fill = new SolidColorPaint(SKColor.FromHsv(i * 360f / Math.Max(1, slices.Count), 44f, 82f)),
                        InnerRadius = 50,
                        HoverPushout = 7,
                        ToolTipLabelFormatter = _ => $"{CombatantRow.Compact(slice.Value)}  ·  {share:P0}",
                    };
                })];
                    DrillCartesianVisible = false;
                    DrillDonutVisible = true;
                    break;
                }
            case 3 when chart.Heat is { } heat:
                {
                    // Heat strip: time on X, intensity = the skill's output in
                    // that window (stone → gold → red).
                    var points = new LiveChartsCore.Defaults.WeightedPoint[heat.Length];
                    for (var i = 0; i < heat.Length; i++)
                        points[i] = new(i, 0, heat[i]);
                    DrillCartesianSeries =
                    [
                        new HeatSeries<LiveChartsCore.Defaults.WeightedPoint>
                    {
                        Values = points,
                        Name = "output",
                        HeatMap =
                        [
                            new LiveChartsCore.Drawing.LvcColor(28, 31, 46),
                            new LiveChartsCore.Drawing.LvcColor(200, 169, 110),
                            new LiveChartsCore.Drawing.LvcColor(248, 113, 113),
                        ],
                    },
                ];
                    DrillXAxes = [TimeAxis(chart.BucketSeconds)];
                    DrillYAxes = [new Axis { Labels = [""], LabelsPaint = null, SeparatorsPaint = null }];
                    DrillCartesianVisible = true;
                    DrillDonutVisible = false;
                    break;
                }
            default:
                DrillChartVisible = false;
                DrillCartesianVisible = false;
                DrillDonutVisible = false;
                return;
        }
        DrillChartVisible = true;
    }

    private static Axis TimeAxis(double bucketSeconds) => new()
    {
        Labeler = v => TimeSpan.FromSeconds(Math.Max(0, v) * bucketSeconds).ToString(@"m\:ss"),
        LabelsPaint = MutedPaint(),
        TextSize = 11,
        SeparatorsPaint = null,
    };

}
