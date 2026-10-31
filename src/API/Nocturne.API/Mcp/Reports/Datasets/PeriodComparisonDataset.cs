using System.Globalization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Mcp.Reports.Datasets;

/// <summary>
/// The web comparison report (<c>/reports/comparison</c>) as a dataset: two periods of local days,
/// each read as that page reads it (the range analysis of <c>GET /api/v4/statistics/range-analytics</c>
/// and, with treatments.read, the insulin delivery figures of <c>insulin-delivery-stats</c>), and one
/// row per figure with both values, the change and whether the change is better or worse.
/// </summary>
internal sealed class PeriodComparisonDataset : IReportDataset
{
    public string Name => PeriodComparisonParameters.Name;

    public string Description =>
        "Period comparison: two periods of whole local days side by side, as the Nocturne comparison report shows them. One "
        + "row per figure: time in each glucose range, GMI, CV, glycemic risk index, mean, time and episodes high and low per day, "
        + "SD, median, LBGI, HBGI, MAGE, CONGA, ADRR, data coverage, and with treatments.read insulin, basal and carbs per day. Each "
        + "row has period A's value, period B's, the change (B minus A), the change as a percent of A, and 'better' or 'worse' "
        + "where a lower or higher value is better; no verdict where neither is (mean glucose, insulin). Send 'preset' "
        + "(last7-prior7, last14-prior14, last30-prior30, thisMonth-lastMonth) or 'a' and 'b'. Draw as comparison or table.";

    public Type ParametersType => typeof(PeriodComparisonParameters);

    public string RequiredScope => Scope.GlucoseRead;

    private static readonly IReadOnlyList<ReportField> RowFields =
    [
        new("metric", "Figure key", ReportFieldKind.Category, null, "The figure's key: tirTarget, gmi, cv and so on."),
        new("group", "Group", ReportFieldKind.Category, null, "The group the figure is listed under."),
        new("label", "Figure", ReportFieldKind.Category, null, "The figure's name."),
        new("a", "Period A", ReportFieldKind.Number, null, "The figure over period A, in the row's unit; empty where A has no data."),
        new("b", "Period B", ReportFieldKind.Number, null, "The figure over period B, in the row's unit; empty where B has no data."),
        new("delta", "Change", ReportFieldKind.Number, null, "B minus A, in the row's unit."),
        new("changePercent", "Change (%)", ReportFieldKind.Number, "%", "The change as a percent of A."),
        new("unit", "Unit", ReportFieldKind.Category, null, "The row's unit: %, mg/dL or mmol/L, min, U, g; empty for an index."),
        new("verdict", "Verdict", ReportFieldKind.Category, null,
            "better or worse: whether B's value is better than A's for this figure; empty where neither a rise nor a fall is better, or nothing changed."),
        new("color", "Color", ReportFieldKind.Category, null, "A glucose range's theme color, for the time in each range rows; their bar is drawn in it."),
    ];

    public IReadOnlyList<ReportField> Fields => RowFields;

    public IReadOnlyList<ReportPrimitive> AllowedPrimitives { get; } = [ReportPrimitive.Comparison, ReportPrimitive.Table];

    public TimeSpan DefaultRange => TimeSpan.FromDays(28);

    /// <summary>The panel's own range is not read; each period is checked against <see cref="PeriodComparisonParameters.MaxPeriodDays"/>.</summary>
    public TimeSpan MaxRange(ReportPrimitive primitive) => TimeSpan.FromDays(PeriodComparisonParameters.MaxPeriodDays);

    public int PointBudget => 40;

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> SampleRows { get; } =
    [
        new Dictionary<string, object?>
        {
            ["metric"] = "inRange", ["group"] = "Time in each range", ["label"] = "In range", ["a"] = 64.2, ["b"] = 70.1, ["delta"] = 5.9,
            ["changePercent"] = 9.2, ["unit"] = "%", ["verdict"] = "better", ["color"] = ChartColor.GlucoseInRange,
        },
        new Dictionary<string, object?>
        {
            ["metric"] = "mean", ["group"] = "Overview", ["label"] = "Mean glucose", ["a"] = 158.0, ["b"] = 151.0, ["delta"] = -7.0,
            ["changePercent"] = -4.4, ["unit"] = "mg/dL", ["verdict"] = null, ["color"] = null,
        },
    ];

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters)
    {
        var p = (PeriodComparisonParameters)parameters;
        if (p.Preset is not null && (p.A is not null || p.B is not null))
            yield return ("preset", "Send 'preset', or 'a' and 'b', not both.");
        else if ((p.A is null) != (p.B is null))
            yield return (p.A is null ? "a" : "b", "Send 'a' and 'b' together, or 'preset' alone.");
        if (p.Preset is { } preset && !Enum.IsDefined(preset))
            yield return ("preset", "Send one of: last7-prior7, last14-prior14, last30-prior30, thisMonth-lastMonth.");
        foreach (var (name, period) in new[] { ("a", p.A), ("b", p.B) })
        {
            if (period is null)
                continue;
            if (period.To < period.From)
                yield return ($"{name}.to", "'to' must not be before 'from'.");
            else if (period.To.DayNumber - period.From.DayNumber + 1 > PeriodComparisonParameters.MaxPeriodDays)
                yield return (name, $"A period covers at most {PeriodComparisonParameters.MaxPeriodDays} days.");
            if (period.From.Year < 2000 || period.To.Year > 2999)
                yield return (name, "Send dates from 2000 on.");
        }
    }

    /// <summary>A period of local days, both ends counted, and its label.</summary>
    internal sealed record Period(string Label, DateOnly From, DateOnly To)
    {
        public int Days => To.DayNumber - From.DayNumber + 1;

        public string Describe() => string.Create(CultureInfo.InvariantCulture, $"{Label}, {From:yyyy-MM-dd} to {To:yyyy-MM-dd}");
    }

    /// <summary>The two periods the parameters name on the calendar whose today is <paramref name="today"/>, as the web report resolves its presets.</summary>
    internal static (Period A, Period B) Periods(PeriodComparisonParameters parameters, DateOnly today)
    {
        if (parameters.A is { } a && parameters.B is { } b)
            return (new Period("Period A", a.From, a.To), new Period("Period B", b.From, b.To));

        if (parameters.Preset == ReportComparisonPreset.ThisMonthLastMonth)
        {
            var thisMonth = new DateOnly(today.Year, today.Month, 1);
            return (new Period("Last month", thisMonth.AddMonths(-1), thisMonth.AddDays(-1)), new Period("This month", thisMonth, today));
        }

        var span = parameters.Preset switch
        {
            ReportComparisonPreset.Last7Prior7 => 7,
            ReportComparisonPreset.Last30Prior30 => 30,
            _ => 14,
        };
        var bFrom = today.AddDays(-(span - 1));
        var aTo = bFrom.AddDays(-1);
        return (new Period($"Prior {span} days", aTo.AddDays(-(span - 1)), aTo), new Period($"Last {span} days", bFrom, today));
    }

    /// <summary>One side's figures: the range analysis, and the insulin delivery figures where read.</summary>
    internal sealed record Side(ExtendedGlucoseAnalytics Analysis, InsulinDeliveryStatistics? Insulin)
    {
        public bool HasReadings => Analysis.BasicStats.Count > 0;
    }

    /// <summary>Which way a figure is better, or neither.</summary>
    internal enum Better { Neither, Higher, Lower }

    /// <summary>One figure of the comparison: its key, group, words, unit, which way is better, its rounding, and how it is read.</summary>
    internal sealed record Metric(
        string Key,
        string Group,
        string Label,
        string Unit,
        Better Better,
        int Digits,
        Func<Side, double?> Get,
        bool Insulin = false,
        ChartColor? Color = null);

    internal const string RangesGroup = "Time in each range";
    internal const string GlucoseUnitToken = "glucose";

    private static double? Variability(Side s, Func<GlycemicVariability, double?> pick) =>
        s.Analysis.GlycemicVariability is { } v ? pick(v) : null;

    /// <summary>The figures, in the web report's groups and order; the time in each range rows carry their band's colour.</summary>
    internal static readonly IReadOnlyList<Metric> Metrics =
    [
        new("veryLow", RangesGroup, "Very low", "%", Better.Lower, 1, s => s.Analysis.TimeInRange.Percentages.VeryLow, Color: ChartColor.GlucoseVeryLow),
        new("low", RangesGroup, "Low", "%", Better.Lower, 1, s => s.Analysis.TimeInRange.Percentages.Low, Color: ChartColor.GlucoseLow),
        new("inRange", RangesGroup, "In range", "%", Better.Higher, 1, s => s.Analysis.TimeInRange.Percentages.Target, Color: ChartColor.GlucoseInRange),
        new("high", RangesGroup, "High", "%", Better.Lower, 1, s => s.Analysis.TimeInRange.Percentages.High, Color: ChartColor.GlucoseHigh),
        new("veryHigh", RangesGroup, "Very high", "%", Better.Lower, 1, s => s.Analysis.TimeInRange.Percentages.VeryHigh, Color: ChartColor.GlucoseVeryHigh),

        new("gmi", "Overview", "GMI", "%", Better.Lower, 2, s => s.Analysis.GMI.Value),
        new("cv", "Overview", "Variability (CV)", "%", Better.Lower, 1, s => Variability(s, v => v.CoefficientOfVariation)),
        new("gri", "Overview", "Glycemic risk index", "", Better.Lower, 0, s => s.Analysis.GRI.Score),
        new("mean", "Overview", "Mean glucose", GlucoseUnitToken, Better.Neither, 0, s => s.Analysis.BasicStats.Mean),
        new("highMinutesPerDay", "Overview", "Time high per day", "min", Better.Lower, 0, s => s.Analysis.TimeInRange.AverageDailyMinutes.High),
        new("highEpisodesPerDay", "Overview", "High episodes per day", "", Better.Lower, 2, s => s.Analysis.TimeInRange.AverageDailyEpisodes.AboveRange),
        new("lowMinutesPerDay", "Overview", "Time low per day", "min", Better.Lower, 0, s => s.Analysis.TimeInRange.AverageDailyMinutes.Low),
        new("lowEpisodesPerDay", "Overview", "Low episodes per day", "", Better.Lower, 2, s => s.Analysis.TimeInRange.AverageDailyEpisodes.BelowRange),

        new("sd", "Variability and risk", "Standard deviation", GlucoseUnitToken, Better.Lower, 0, s => Variability(s, v => v.StandardDeviation)),
        new("median", "Variability and risk", "Median glucose", GlucoseUnitToken, Better.Neither, 0, s => s.Analysis.BasicStats.Median),
        new("lbgi", "Variability and risk", "LBGI", "", Better.Lower, 1, s => Variability(s, v => v.LowBloodGlucoseIndex)),
        new("hbgi", "Variability and risk", "HBGI", "", Better.Lower, 1, s => Variability(s, v => v.HighBloodGlucoseIndex)),
        new("mage", "Variability and risk", "MAGE", GlucoseUnitToken, Better.Lower, 0, s => Variability(s, v => v.MeanAmplitudeGlycemicExcursions)),
        new("conga", "Variability and risk", "CONGA", GlucoseUnitToken, Better.Lower, 0, s => Variability(s, v => v.ContinuousOverlappingNetGlycemicAction)),
        new("adrr", "Variability and risk", "ADRR", "", Better.Lower, 1, s => Variability(s, v => v.AverageDailyRiskRange)),

        new("insulinPerDay", "Insulin and carbs", "Total insulin per day", "U", Better.Neither, 1,
            s => s.Insulin is { InsulinEventCount: > 0 } i ? i.Tdd : null, Insulin: true),
        new("basalPerDay", "Insulin and carbs", "Basal per day", "U", Better.Neither, 1,
            s => s.Insulin is { InsulinEventCount: > 0 } i ? i.TotalBasalPerDay : null, Insulin: true),
        new("basalPercent", "Insulin and carbs", "Basal share of insulin", "%", Better.Neither, 1,
            s => s.Insulin is { InsulinEventCount: > 0 } i ? i.BasalPercent : null, Insulin: true),
        new("bolusesPerDay", "Insulin and carbs", "Manual boluses per day", "", Better.Neither, 1,
            s => s.Insulin is { InsulinEventCount: > 0 } i ? i.BolusesPerDay : null, Insulin: true),
        new("carbsPerDay", "Insulin and carbs", "Carbs per day", "g", Better.Neither, 0,
            s => s.Insulin is { CarbCount: > 0 } i ? i.CarbsPerDay : null, Insulin: true),
        new("icr", "Insulin and carbs", "Carb ratio", "g/U", Better.Neither, 1, s => s.Insulin?.IcRatio, Insulin: true),

        new("cgmActivePercent", "Data quality", "CGM active time", "%", Better.Higher, 1, s => s.Analysis.DataQuality.CgmActivePercent),
        new("daysWithData", "Data quality", "Days with data", "", Better.Neither, 0, s => s.Analysis.DataSufficiency.DaysWithData),
        new("readingsPerDay", "Data quality", "Readings per day", "", Better.Neither, 0, s => s.Analysis.DataSufficiency.AverageReadingsPerDay),
    ];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var parameters = (PeriodComparisonParameters)request.Parameters;
        var clock = await request.Services.GetRequiredService<TenantClockResolver>().ResolveAsync(cancellationToken);
        var zone = clock.Zone ?? TimeZoneInfo.Utc;
        var now = request.Now ?? request.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var (a, b) = Periods(parameters, today);
        var window = new ReportWindow(Start(a.From, zone).ToUnixTimeMilliseconds(), Start(b.To.AddDays(1), zone).ToUnixTimeMilliseconds());
        var labels = new Dictionary<string, string> { ["a"] = a.Describe(), ["b"] = b.Describe() };

        if (request.HistoryClamped)
            return new ReportDatasetOutput
            {
                Rows = [],
                Summary = Head(a, b, clock.Zone is null) + "\nNot compared: this token reads only the last 24 hours, so it cannot read either period whole.",
                Window = window,
                FieldLabels = labels,
            };

        var treatments = request.HasScope(Scope.TreatmentsRead);
        var sideA = await SideAsync(request, a, zone, treatments, cancellationToken);
        var sideB = await SideAsync(request, b, zone, treatments, cancellationToken);
        var rows = Rows(sideA, sideB, treatments, request.UseMmol);

        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = Summarise(a, b, rows, sideA.HasReadings, sideB.HasReadings, treatments, clock.Zone is null),
            Window = window,
            FieldLabels = labels,
            LinkQuery =
            [
                new("preset", "custom"),
                new("aFrom", a.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new("aTo", a.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new("aLabel", a.Label),
                new("bFrom", b.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new("bTo", b.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new("bLabel", b.Label),
            ],
        };
    }

    private static DateTimeOffset Start(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(zone.IsInvalidTime(local) ? local.AddHours(1) : local, zone), TimeSpan.Zero);
    }

    /// <summary>A period read as the comparison report reads it: the range analysis over its local days, and the insulin figures where the token reads treatments.</summary>
    private static async Task<Side> SideAsync(ReportDatasetRequest request, Period period, TimeZoneInfo zone, bool treatments, CancellationToken ct)
    {
        var side = request with { From = Start(period.From, zone), To = Start(period.To.AddDays(1), zone) };
        var analysis = await side.Cache.AnalysisAsync(side, ct);
        var insulin = treatments ? await GlucoseStatisticsDataset.InsulinAsync(side, ct) : null;
        return new Side(analysis, insulin);
    }

    /// <summary>
    /// One row per figure, insulin figures only for a token that reads treatments. A side with no readings has
    /// no glucose figures, as the web report reads an analysis of zeros as absent.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(Side a, Side b, bool treatments, bool useMmol)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var metric in Metrics)
        {
            if (metric.Insulin && !treatments)
                continue;
            var glucose = metric.Unit == GlucoseUnitToken;
            var digits = glucose && useMmol ? 1 : metric.Digits;
            double? Read(Side side)
            {
                if (!metric.Insulin && !side.HasReadings)
                    return null;
                var value = metric.Get(side);
                if (value is not { } v || double.IsNaN(v) || double.IsInfinity(v))
                    return null;
                return Math.Round(glucose ? GlucoseUnits.Scale(v, useMmol) : v, digits);
            }

            var va = Read(a);
            var vb = Read(b);
            double? delta = va is { } x && vb is { } y ? Math.Round(y - x, digits) : null;
            double? change = va is { } from && vb is { } to
                ? Math.Round(from == 0 ? (to == from ? 0 : Math.Sign(to - from) * 100) : (to - from) / Math.Abs(from) * 100, 1)
                : null;
            var verdict = delta is { } d && d != 0 && metric.Better != Better.Neither
                ? (d > 0) == (metric.Better == Better.Higher) ? "better" : "worse"
                : null;

            rows.Add(new Dictionary<string, object?>
            {
                ["metric"] = metric.Key,
                ["group"] = metric.Group,
                ["label"] = metric.Label,
                ["a"] = va,
                ["b"] = vb,
                ["delta"] = delta,
                ["changePercent"] = change,
                ["unit"] = glucose ? (useMmol ? "mmol/L" : "mg/dL") : metric.Unit,
                ["verdict"] = verdict,
                ["color"] = metric.Color,
            });
        }
        return rows;
    }

    private static string Head(Period a, Period b, bool utc) =>
        $"Period comparison: A is {a.Describe()} ({a.Days} days); B is {b.Describe()} ({b.Days} days)"
        + (utc ? ", in UTC days: the tenant has no time zone set." : ", in the tenant's local days.");

    internal static string Summarise(
        Period a,
        Period b,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        bool aReadings,
        bool bReadings,
        bool treatments,
        bool utc)
    {
        var lines = new List<string> { Head(a, b, utc) };
        if (!aReadings)
            lines.Add("Period A has no glucose readings.");
        if (!bReadings)
            lines.Add("Period B has no glucose readings.");

        string F(object? value) => value is double d ? d.ToString("0.##", CultureInfo.InvariantCulture) : "no data";
        foreach (var row in rows)
        {
            if (row["a"] is null && row["b"] is null)
                continue;
            var unit = row["unit"] is string { Length: > 0 } u ? (u == "%" ? "%" : " " + u) : "";
            var change = row["delta"] is double d
                ? $" (change {(d > 0 ? "+" : "")}{F(d)}{unit}{(row["verdict"] is string v ? $", {v}" : "")})"
                : "";
            lines.Add($"{row["label"]}: {F(row["a"])}{(row["a"] is null ? "" : unit)} in A, {F(row["b"])}{(row["b"] is null ? "" : unit)} in B{change}.");
        }
        if (!treatments)
            lines.Add("Insulin and carb figures are not shown: they need the treatments.read scope, which this token does not hold.");
        return string.Join('\n', lines);
    }
}
