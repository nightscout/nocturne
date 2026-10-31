using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.API.Mcp.Tools;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Mcp.Reports.Datasets;

/// <summary>
/// Glucose readings with the target range and the insulin, carbs and basal at each reading, read
/// through the dashboard chart (<see cref="IChartDataService"/>) and redacted as its endpoint
/// redacts (<see cref="ChartDataReadScopeGuard"/>).
/// </summary>
internal sealed class GlucoseSeriesDataset : IReportDataset
{
    public const int Budget = 2000;

    /// <summary>The dashboard chart carries every marker and span, so drawn whole it covers less.</summary>
    internal static readonly TimeSpan ChartMaxRange = TimeSpan.FromDays(3);

    public string Name => GlucoseSeriesParameters.Name;

    public string Description =>
        "Glucose readings over time, in the user's display unit and in mg/dL, with the trend arrow and the "
        + "target range at each reading. With treatments.read, also insulin on board, carbs on board and the "
        + "basal rate at each reading. Draw as time_series (lines), table, or glucose_chart (the full dashboard "
        + "chart with boluses, carbs, basal, pump modes and profiles). A time_series panel of at most 72 hours can "
        + "draw bolus and carb markers on the line ('markers').";

    /// <summary>The longest range a panel draws markers over: every marker is sent, as the dashboard chart sends them.</summary>
    internal static readonly TimeSpan MarkerMaxRange = TimeSpan.FromHours(72);

    public Type ParametersType => typeof(GlucoseSeriesParameters);

    public string RequiredScope => Scope.GlucoseRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("time", "Time", ReportFieldKind.Time, null, "When the reading was taken, Unix milliseconds."),
        new("glucose", "Glucose", ReportFieldKind.Number, ReportField.GlucoseUnit,
            "The reading in the user's display unit (mg/dL, or mmol/L to one decimal).",
            Color: ChartColor.GlucoseInRange, GlucoseScale: true),
        new("glucoseMgdl", "Glucose (mg/dL)", ReportFieldKind.Number, "mg/dL", "The reading in mg/dL.",
            Color: ChartColor.GlucoseInRange),
        new("direction", "Trend", ReportFieldKind.Category, null,
            "The trend arrow: DoubleUp, SingleUp, FortyFiveUp, Flat, FortyFiveDown, SingleDown, DoubleDown, or empty."),
        new("targetLow", "Range low", ReportFieldKind.Number, ReportField.GlucoseUnit,
            "The lower limit of the in-range band, 70 mg/dL (3.9 mmol/L), the clinical consensus limit the reports use. Not the user's profile target.", Color: ChartColor.GlucoseLow),
        new("targetHigh", "Range high", ReportFieldKind.Number, ReportField.GlucoseUnit,
            "The upper limit of the in-range band, 180 mg/dL (10.0 mmol/L), the clinical consensus limit the reports use. Not the user's profile target.", Color: ChartColor.GlucoseHigh),
        new("iob", "IOB", ReportFieldKind.Number, "U", "Insulin on board at the reading.",
            Scope.TreatmentsRead, ChartColor.InsulinBolus),
        new("cob", "COB", ReportFieldKind.Number, "g", "Carbs on board at the reading.",
            Scope.TreatmentsRead, ChartColor.Carbs),
        new("basalRate", "Basal rate", ReportFieldKind.Number, "U/h", "The basal rate running at the reading.",
            Scope.TreatmentsRead, ChartColor.InsulinBasal),
    ];

    public IReadOnlyList<ReportPrimitive> AllowedPrimitives { get; } =
        [ReportPrimitive.TimeSeries, ReportPrimitive.Table, ReportPrimitive.GlucoseChart];

    public TimeSpan DefaultRange => TimeSpan.FromHours(6);

    public TimeSpan MaxRange(ReportPrimitive primitive) =>
        primitive == ReportPrimitive.GlucoseChart ? ChartMaxRange : TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => Budget;

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> SampleRows { get; } =
    [
        new Dictionary<string, object?>
        {
            ["time"] = 1793260800000L, ["glucose"] = 112.0, ["glucoseMgdl"] = 112.0, ["direction"] = "Flat",
            ["targetLow"] = 70.0, ["targetHigh"] = 180.0, ["iob"] = 1.25, ["cob"] = 12.0, ["basalRate"] = 0.85,
        },
        new Dictionary<string, object?>
        {
            ["time"] = 1793261100000L, ["glucose"] = 118.0, ["glucoseMgdl"] = 118.0, ["direction"] = "FortyFiveUp",
            ["targetLow"] = 70.0, ["targetHigh"] = 180.0, ["iob"] = 1.18, ["cob"] = 10.0, ["basalRate"] = 0.85,
        },
        new Dictionary<string, object?>
        {
            ["time"] = 1793261400000L, ["glucose"] = 126.0, ["glucoseMgdl"] = 126.0, ["direction"] = "FortyFiveUp",
            ["targetLow"] = 70.0, ["targetHigh"] = 180.0, ["iob"] = 1.12, ["cob"] = 8.0, ["basalRate"] = 1.1,
        },
    ];

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters)
    {
        var p = (GlucoseSeriesParameters)parameters;
        if (p.IntervalMinutes is < 1 or > 60)
            yield return ("intervalMinutes", $"{p.IntervalMinutes} is out of range. Send 1 to 60.");
        if (p.Markers is { } markers && markers.Distinct().Count() != markers.Count)
            yield return ("markers", "A marker is named twice.");
    }

    public IEnumerable<(string Field, string Message)> ValidatePanel(ReportDatasetParameters parameters, ReportPrimitive primitive, TimeSpan span)
    {
        if (((GlucoseSeriesParameters)parameters).Markers is not { Count: > 0 })
            yield break;
        if (primitive != ReportPrimitive.TimeSeries)
            yield return ("markers", $"Only a time_series panel draws markers. This panel is {primitive.WireName()}. Remove 'markers'.");
        else if (span > MarkerMaxRange)
            yield return ("markers",
                $"Markers cover at most {ReportSpecValidator.Describe(MarkerMaxRange)}. This range is {ReportSpecValidator.Describe(span)}. "
                + "Shorten the range, or remove 'markers'.");
    }

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var services = request.Services;
        var parameters = (GlucoseSeriesParameters)request.Parameters;
        var chart = request.Primitive == ReportPrimitive.GlucoseChart;
        var interval = IntervalFor(request.From, request.To, parameters.IntervalMinutes, Budget);

        var data = await services.GetRequiredService<IChartDataService>().GetDashboardChartDataAsync(
            request.From.ToUnixTimeMilliseconds(),
            request.To.ToUnixTimeMilliseconds(),
            Math.Min(interval, 60),
            includeHealthSeries: chart,
            cancellationToken);
        data = ChartDataReadScopeGuard.Redact(data, request.GrantedScopes);
        if (!TrackerReadAccess.Allows(services.GetRequiredService<IHttpContextAccessor>().HttpContext!))
            data.TrackerMarkers = [];

        var treatments = request.HasScope(Scope.TreatmentsRead);
        if (chart)
        {
            GlucoseTools.RecordReads(data, request.ReadLog);
        }
        IReadOnlyList<ReportMarker> markers = chart ? [] : Markers(data, parameters.Markers);
        if (!chart)
        {
            if (data.GlucoseData.Count > 0)
                request.ReadLog.Record("glucose", data.GlucoseData.Count);
            var series = data.IobSeries.Count + data.CobSeries.Count + data.BasalSeries.Count + markers.Count;
            if (treatments && series > 0)
                request.ReadLog.Record("treatments", series);
        }

        var omitted = treatments
            ? []
            : Fields.Where(f => f.Scope == Scope.TreatmentsRead).Select(f => new ReportOmittedField(f.Name, Scope.TreatmentsRead)).ToList();

        var statistics = services.GetRequiredService<IStatisticsService>();
        var summary = GlucoseSummary.Build(
            data,
            statistics,
            request.UseMmol,
            request.From,
            request.To,
            chart ? GlucoseSummary.OmittedTracks(request.HasScope) : null,
            chart ? null : [.. omitted.Select(o => o.Field)]);
        if (parameters.Markers is { Count: > 0 } asked)
            summary += '\n' + MarkerNote(asked, markers, treatments);

        var thresholds = GlucoseSummary.Thresholds(data.Thresholds);
        var sourceRows = data.GlucoseData.Count;
        var sampled = chart ? [] : Downsample(data.GlucoseData, request.From.ToUnixTimeMilliseconds(), interval);
        // The dashboard chart draws every reading, as the dashboard does; only more than the budget is thinned.
        var chartInterval = sourceRows > Budget ? IntervalFor(request.From, request.To, 1, Budget) : 0;
        if (chart && chartInterval > 0)
            data.GlucoseData = Downsample(data.GlucoseData, request.From.ToUnixTimeMilliseconds(), chartInterval);
        var rows = chart ? data.GlucoseData.Count : sampled.Count;

        return new ReportDatasetOutput
        {
            Rows = chart ? [] : Rows(sampled, data, thresholds, request.UseMmol),
            Summary = summary,
            Omitted = omitted,
            OmittedScopes = GlucoseSummary.OmittedScopes(request.HasScope),
            Thresholds = ReportThresholds.Of(thresholds, request.UseMmol),
            Chart = chart ? data : null,
            Markers = markers,
            Resolution = new ReportResolution(
                parameters.IntervalMinutes,
                chart ? (chartInterval > 0 ? chartInterval : parameters.IntervalMinutes) : interval,
                sourceRows,
                rows,
                rows < sourceRows),
        };
    }

    /// <summary>
    /// The dashboard's bolus and carb markers of the kinds asked for. The chart payload is already
    /// redacted, so without treatments.read there are none.
    /// </summary>
    internal static IReadOnlyList<ReportMarker> Markers(DashboardChartData data, IReadOnlyList<ReportMarkerKind>? kinds)
    {
        if (kinds is not { Count: > 0 })
            return [];
        var markers = new List<ReportMarker>();
        if (kinds.Contains(ReportMarkerKind.Bolus))
            markers.AddRange(data.BolusMarkers.Select(b => new ReportMarker(
                b.Time, ReportMarkerKind.Bolus, Math.Round(b.Insulin, 2), b.BolusType is BolusType.Smb or BolusType.AutomaticBolus)));
        if (kinds.Contains(ReportMarkerKind.Carbs))
            markers.AddRange(data.CarbMarkers.Select(c => new ReportMarker(c.Time, ReportMarkerKind.Carbs, Math.Round(c.Carbs, 1))));
        return [.. markers.OrderBy(m => m.Time)];
    }

    internal static string MarkerNote(IReadOnlyList<ReportMarkerKind> asked, IReadOnlyList<ReportMarker> markers, bool treatments)
    {
        var words = asked.Select(k => k == ReportMarkerKind.Bolus ? "bolus" : "carbs").ToList();
        if (!treatments)
            return $"Markers omitted: {string.Join(", ", words)} (scope not granted: treatments.read).";
        var boluses = markers.Count(m => m.Kind == ReportMarkerKind.Bolus);
        var carbs = markers.Count(m => m.Kind == ReportMarkerKind.Carbs);
        var parts = new List<string>();
        if (asked.Contains(ReportMarkerKind.Bolus))
            parts.Add($"{boluses} {(boluses == 1 ? "bolus" : "boluses")}");
        if (asked.Contains(ReportMarkerKind.Carbs))
            parts.Add($"{carbs} carb {(carbs == 1 ? "entry" : "entries")}");
        return $"Markers drawn: {string.Join(", ", parts)}.";
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
}
