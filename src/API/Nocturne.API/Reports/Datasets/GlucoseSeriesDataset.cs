using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// Glucose readings with the target range and the insulin, carbs and basal at each reading, read
/// through the dashboard chart's stages (<see cref="IChartDataService"/>) and redacted as its endpoint
/// redacts (<see cref="ChartDataReadScopeGuard"/>).
/// </summary>
internal sealed class GlucoseSeriesDataset : IReportDataset
{
    public const int Budget = 2000;

    public string Name => GlucoseSeriesParameters.Name;

    public string Label => "Glucose readings";

    public string Description => "Glucose readings over time, with insulin on board, carbs on board and the basal rate.";

    public Type ParametersType => typeof(GlucoseSeriesParameters);

    public string RequiredScope => Scope.GlucoseRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("time", "Time", ReportFieldKind.Time, null, "When the reading was taken, Unix milliseconds."),
        new("glucose", "Glucose", ReportFieldKind.Number, ReportField.GlucoseUnit,
            "The reading in the user's display unit (mg/dL, or mmol/L to one decimal).",
            Color: ChartColor.GlucoseInRange, GlucoseScale: true, Aggregate: ReportAggregate.Mean),
        new("glucoseMgdl", "Glucose (mg/dL)", ReportFieldKind.Number, "mg/dL", "The reading in mg/dL.",
            Color: ChartColor.GlucoseInRange, Aggregate: ReportAggregate.Mean),
        new("direction", "Trend", ReportFieldKind.Category, null,
            "The trend arrow: DoubleUp, SingleUp, FortyFiveUp, Flat, FortyFiveDown, SingleDown, DoubleDown, or empty."),
        new("targetLow", "Range low", ReportFieldKind.Number, ReportField.GlucoseUnit,
            "The lower limit of the in-range band, 70 mg/dL (3.9 mmol/L), the clinical consensus limit the reports use. Not the user's profile target.",
            Color: ChartColor.GlucoseInRange, Step: true, Aggregate: ReportAggregate.Last),
        new("targetHigh", "Range high", ReportFieldKind.Number, ReportField.GlucoseUnit,
            "The upper limit of the in-range band, 180 mg/dL (10.0 mmol/L), the clinical consensus limit the reports use. Not the user's profile target.",
            Color: ChartColor.GlucoseInRange, Step: true, Aggregate: ReportAggregate.Last),
        new("iob", "IOB", ReportFieldKind.Number, "U", "Insulin on board at the reading.",
            Scope.TreatmentsRead, ChartColor.InsulinBolus, Aggregate: ReportAggregate.Mean),
        new("cob", "COB", ReportFieldKind.Number, "g", "Carbs on board at the reading.",
            Scope.TreatmentsRead, ChartColor.Carbs, Aggregate: ReportAggregate.Mean),
        new("basalRate", "Basal rate", ReportFieldKind.Number, "U/h", "The basal rate running at the reading.",
            Scope.TreatmentsRead, ChartColor.InsulinBasal, Step: true, Aggregate: ReportAggregate.Last),
    ];

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Line, ReportMark.Points, ReportMark.Area, ReportMark.Band];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) => mark switch
    {
        ReportMark.Band => new ReportLayerBindings { X = "time", Edges = ["targetLow", "targetHigh"] },
        ReportMark.Line or ReportMark.Points or ReportMark.Area => new ReportLayerBindings { X = "time", Y = ["glucose"] },
        _ => null,
    };

    public TimeSpan DefaultRange => TimeSpan.FromHours(6);

    public TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => Budget;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var data = await request.Cache.ChartAsync(request, cancellationToken);
        var treatments = request.HasScope(Scope.TreatmentsRead);
        var interval = IntervalFor(request.From, request.To, 1, Budget);
        var sampled = Downsample(data.GlucoseData, request.From.ToUnixTimeMilliseconds(), interval);

        var omitted = treatments
            ? []
            : Fields.Where(f => f.Scope == Scope.TreatmentsRead).Select(f => new ReportOmittedField(f.Name, Scope.TreatmentsRead)).ToList();

        var statistics = request.Services.GetRequiredService<IStatisticsService>();
        var thresholds = GlucoseSummary.Thresholds(data.Thresholds);
        var (facts, sentence, context) = GlucoseSummary.Facts(data, statistics, request.UseMmol, request.From, request.To, request.Zone);
        return new ReportDatasetOutput
        {
            Rows = Rows(sampled, data, thresholds, request.UseMmol),
            Summary = GlucoseSummary.Build(data, statistics, request.UseMmol, request.From, request.To, [.. omitted.Select(o => o.Field)]),
            Facts = facts,
            Sentence = sentence,
            Context = context,
            Omitted = omitted,
            Thresholds = ReportThresholds.Of(thresholds, request.UseMmol),
            Resolution = new ReportResolution(1, interval, data.GlucoseData.Count, sampled.Count, sampled.Count < data.GlucoseData.Count),
        };
    }

    /// <summary>
    /// The row spacing: the interval asked for, or the coarser one that keeps a series of
    /// <paramref name="from"/> to <paramref name="to"/> within <paramref name="budget"/> rows.
    /// </summary>
    internal static int IntervalFor(DateTimeOffset from, DateTimeOffset to, int requested, int budget) =>
        Math.Max(requested, (int)Math.Ceiling((to - from).TotalMinutes / budget));

    /// <summary>
    /// The first reading in each <paramref name="intervalMinutes"/> slot counted from
    /// <paramref name="fromMills"/>: real readings, none averaged, so a low stays as low as it was read.
    /// </summary>
    internal static List<GlucosePointDto> Downsample(IEnumerable<GlucosePointDto> readings, long fromMills, int intervalMinutes)
    {
        var slot = intervalMinutes * 60_000L;
        var kept = new List<GlucosePointDto>();
        long? last = null;
        foreach (var reading in readings.OrderBy(r => r.Time))
        {
            var index = Math.Max(0, reading.Time - fromMills) / slot;
            if (index == last)
                continue;
            kept.Add(reading);
            last = index;
        }
        return kept;
    }

    private static List<IReadOnlyDictionary<string, object?>> Rows(
        List<GlucosePointDto> readings,
        DashboardChartData data,
        GlycemicThresholds thresholds,
        bool useMmol)
    {
        var low = GlucoseUnits.Scale(thresholds.TargetBottom, useMmol);
        var high = GlucoseUnits.Scale(thresholds.TargetTop, useMmol);
        var iob = data.IobSeries.OrderBy(p => p.Timestamp).ToList();
        var cob = data.CobSeries.OrderBy(p => p.Timestamp).ToList();
        var basal = data.BasalSeries.OrderBy(p => p.Timestamp).ToList();

        return [.. readings.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["time"] = r.Time,
            ["glucose"] = GlucoseUnits.Scale(r.Sgv, useMmol),
            ["glucoseMgdl"] = Math.Round(r.Sgv),
            ["direction"] = r.Direction,
            ["targetLow"] = low,
            ["targetHigh"] = high,
            ["iob"] = AtOrBefore(iob, r.Time, p => p.Timestamp, p => Math.Round(p.Value, 2)),
            ["cob"] = AtOrBefore(cob, r.Time, p => p.Timestamp, p => Math.Round(p.Value, 1)),
            ["basalRate"] = AtOrBefore(basal, r.Time, p => p.Timestamp, p => Math.Round(p.Rate, 3)),
        })];
    }

    /// <summary>The value of the last point of <paramref name="series"/> at or before <paramref name="time"/>.</summary>
    internal static double? AtOrBefore<T>(List<T> series, long time, Func<T, long> timestamp, Func<T, double> value)
    {
        int lo = 0, hi = series.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (timestamp(series[mid]) <= time)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found < 0 ? null : value(series[found]);
    }

    /// <summary>
    /// Every dataset the redacted payload carries, with its record count. Redaction has already
    /// emptied what the token's scopes do not cover, so an empty dataset is left out.
    /// </summary>
    internal static void RecordReads(DashboardChartData data, ReportReadLog readLog)
    {
        void Read(string dataset, int records)
        {
            if (records > 0)
                readLog.Record(dataset, records);
        }

        Read("glucose", data.GlucoseData.Count + data.BgCheckMarkers.Count);
        Read("treatments",
            data.BolusMarkers.Count + data.CarbMarkers.Count + data.BasalInjectionMarkers.Count
            + data.BasalSeries.Count + data.IobSeries.Count + data.CobSeries.Count
            + data.TempBasalSpans.Count + data.BasalDeliverySpans.Count + data.OverrideSpans.Count
            + data.ActivitySpans.Count(s => s.Kind != ChartSpanKind.Sleep));
        Read("device_events", data.DeviceEventMarkers.Count + data.SystemEventMarkers.Count);
        Read("pump_modes", data.PumpModeSpans.Count);
        Read("profiles", data.ProfileSpans.Count);
        Read("heart_rate", data.HeartRateSeries.Count);
        Read("steps", data.StepSeries.Count);
        Read("sleep", data.ActivitySpans.Count(s => s.Kind == ChartSpanKind.Sleep));
        Read("trackers", data.TrackerMarkers.Count);
    }
}
