using System.Globalization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Mcp.Reports.Datasets;

/// <summary>
/// One row per local day, as the calendar report's punch card computes it
/// (<c>GET /api/v4/statistics/punch-card</c>, <see cref="IStatisticsService.CalculatePunchCard"/>):
/// time in each band, mean glucose and, with treatments.read, the day's insulin and carbs.
/// </summary>
/// <remarks>
/// The punch card also carries each day's readings; they are not served. Without treatments.read no
/// insulin or carb record is read, and the insulin and carb columns are omitted.
/// </remarks>
internal sealed class DailySummaryDataset : IReportDataset
{
    public string Name => DailySummaryParameters.Name;

    public string Description =>
        "Daily summary: one row per local day of the range: time in range, below and above range (percent of the day's "
        + "covered time), mean glucose, the readings, and with treatments.read the day's total insulin (bolus and basal) "
        + "and carbs. The figures of the Nocturne calendar report. Draw as table, bar (x 'date') or time_series (x 'day'). "
        + "Needs the tenant's time zone to place the days.";

    public Type ParametersType => typeof(DailySummaryParameters);

    public string RequiredScope => Scope.GlucoseRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("date", "Date", ReportFieldKind.Category, null, "The local date, yyyy-MM-dd."),
        new("day", "Day", ReportFieldKind.Time, null, "Local midnight at the start of the day, Unix milliseconds."),
        new("readings", "Readings", ReportFieldKind.Number, null, "The day's glucose readings."),
        new("timeInRange", "In range", ReportFieldKind.Percent, "%", "Percent of the day's covered time in range.", Color: ChartColor.GlucoseInRange),
        new("timeBelow", "Below range", ReportFieldKind.Percent, "%", "Percent of the day's covered time below range (low and very low).", Color: ChartColor.GlucoseLow),
        new("timeAbove", "Above range", ReportFieldKind.Percent, "%", "Percent of the day's covered time above range (high and very high).", Color: ChartColor.GlucoseHigh),
        new("mean", "Mean glucose", ReportFieldKind.Number, ReportField.GlucoseUnit, "The mean of the day's readings.", Color: ChartColor.GlucoseInRange),
        new("totalInsulin", "Insulin", ReportFieldKind.Number, "U", "The day's insulin, bolus and basal.", Scope.TreatmentsRead, ChartColor.InsulinBolus),
        new("totalBolus", "Bolus", ReportFieldKind.Number, "U", "The day's bolus insulin, the algorithm's included.", Scope.TreatmentsRead, ChartColor.InsulinBolus),
        new("totalBasal", "Basal", ReportFieldKind.Number, "U", "The day's basal insulin.", Scope.TreatmentsRead, ChartColor.InsulinBasal),
        new("totalCarbs", "Carbs", ReportFieldKind.Number, "g", "The day's carbs.", Scope.TreatmentsRead, ChartColor.Carbs),
    ];

    /// <summary>The fields read from insulin and carb records.</summary>
    internal static readonly IReadOnlyList<string> TreatmentFields = ["totalInsulin", "totalBolus", "totalBasal", "totalCarbs"];

    public ReportBindings? DefaultBindings(ReportPrimitive primitive) => primitive switch
    {
        ReportPrimitive.Bar => new ReportBindings { X = "date", Y = ["timeBelow", "timeInRange", "timeAbove"], Stacked = true },
        ReportPrimitive.TimeSeries => new ReportBindings { X = "day", Y = ["timeInRange"] },
        _ => null,
    };

    public IReadOnlyList<ReportPrimitive> AllowedPrimitives { get; } = [ReportPrimitive.Table, ReportPrimitive.Bar, ReportPrimitive.TimeSeries];

    public TimeSpan DefaultRange => TimeSpan.FromDays(14);

    public TimeSpan MaxRange(ReportPrimitive primitive) => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => V4ReadLimits.MaxAnalyticsSpanDays + 1;

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> SampleRows { get; } =
    [
        new Dictionary<string, object?>
        {
            ["date"] = "2026-10-28", ["day"] = 1793145600000L, ["readings"] = 287, ["timeInRange"] = 74.2, ["timeBelow"] = 1.4,
            ["timeAbove"] = 24.4, ["mean"] = 148.0, ["totalInsulin"] = 41.6, ["totalBolus"] = 21.3, ["totalBasal"] = 20.3, ["totalCarbs"] = 172.0,
        },
    ];

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var services = request.Services;
        var treatments = request.HasScope(Scope.TreatmentsRead);
        IReadOnlyList<ReportOmittedField> omitted = treatments
            ? []
            : [.. TreatmentFields.Select(f => new ReportOmittedField(f, Scope.TreatmentsRead))];

        var clock = await services.GetRequiredService<TenantClockResolver>().ResolveAsync(cancellationToken);
        if (clock.Zone is not { } zone)
            return new ReportDatasetOutput
            {
                Rows = [],
                Summary = $"Daily summary: no data. The days cannot be placed without a time zone ({clock.Reason}). Set one in the profile.",
                Omitted = omitted,
            };

        var startDate = LocalSlotBinning.LocalDay(request.From.ToUnixTimeMilliseconds(), zone);
        var endDate = LocalSlotBinning.LocalDay(request.To.ToUnixTimeMilliseconds() - 1, zone);
        var (startUtc, endUtc) = StatisticsService.PunchCardRange(startDate, endDate, zone);

        var raw = await services.GetRequiredService<ISensorGlucoseRepository>().GetForAnalyticsAsync(
            startUtc, endUtc, null, null, int.MaxValue, descending: false, ct: cancellationToken);
        var glucose = await services.GetRequiredService<ICanonicalGlucoseService>().SelectAsync(raw.ToList(), cancellationToken);
        var records = treatments
            ? await InsulinRecords.ReadAsync(services, startUtc, endUtc, cancellationToken)
            : new InsulinRecords([], [], [], [], []);

        var card = services.GetRequiredService<IStatisticsService>().CalculatePunchCard(
            glucose, records.ManualBoluses, records.AlgorithmBoluses, records.Carbs, records.TempBasals, records.BasalInjections,
            startDate, endDate, zone, services.GetRequiredService<TimeProvider>().GetUtcNow(), cancellationToken);

        if (glucose.Count > 0)
            request.ReadLog.Record("glucose", glucose.Count);
        if (records.Count > 0)
            request.ReadLog.Record("treatments", records.Count);

        var useMmol = request.UseMmol;
        var days = card.Months.SelectMany(m => m.Days).OrderBy(d => d.Timestamp).ToList();
        var rows = days.Select(d =>
        {
            var any = d.TotalReadings > 0;
            double? P(double percent) => any ? Math.Round(percent, 1) : null;
            var row = new Dictionary<string, object?>
            {
                ["date"] = d.Date,
                ["day"] = d.Timestamp,
                ["readings"] = d.TotalReadings,
                ["timeInRange"] = P(d.InRangePercent),
                ["timeBelow"] = P(d.VeryLowPercent + d.LowPercent),
                ["timeAbove"] = P(d.HighPercent + d.VeryHighPercent),
                ["mean"] = any ? GlucoseUnits.Scale(d.AverageGlucose, useMmol) : null,
            };
            if (treatments)
            {
                row["totalInsulin"] = Math.Round(d.TotalInsulin, 1);
                row["totalBolus"] = Math.Round(d.TotalBolus, 1);
                row["totalBasal"] = Math.Round(d.TotalBasal, 1);
                row["totalCarbs"] = Math.Round(d.TotalCarbs, 0);
            }
            return (IReadOnlyDictionary<string, object?>)row;
        }).ToList();

        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = Summarise(rows, startDate, endDate, useMmol, [.. omitted.Select(o => o.Field)]),
            Omitted = omitted,
            Window = days.Count == 0 ? null : new ReportWindow(days[0].Timestamp, days[^1].EndTimestamp),
        };
    }

    /// <summary>The days the rows cover, and the extremes across them; no figure is averaged across days.</summary>
    internal static string Summarise(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        DateOnly start,
        DateOnly end,
        bool useMmol,
        IReadOnlyList<string> omitted)
    {
        string N(object? value, string format) => value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : "no data";
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Daily summary over local days {start:yyyy-MM-dd} to {end:yyyy-MM-dd}."),
        };

        var withData = rows.Where(r => r["readings"] is int n && n > 0).ToList();
        lines.Add($"Days with glucose readings: {withData.Count} of {rows.Count}.");
        if (withData.Count > 0)
        {
            var unit = useMmol ? "mmol/L" : "mg/dL";
            var glucose = useMmol ? "0.0" : "0";
            var best = withData.MaxBy(r => (double)r["timeInRange"]!)!;
            var worst = withData.MinBy(r => (double)r["timeInRange"]!)!;
            var mostBelow = withData.MaxBy(r => (double)r["timeBelow"]!)!;
            var highest = withData.MaxBy(r => (double)r["mean"]!)!;
            lines.Add($"Time in range by day: highest {N(best["timeInRange"], "0.#")}% on {best["date"]}, lowest {N(worst["timeInRange"], "0.#")}% on {worst["date"]}.");
            lines.Add((double)mostBelow["timeBelow"]! > 0
                ? $"Most time below range: {N(mostBelow["timeBelow"], "0.#")}% on {mostBelow["date"]}."
                : "No time below range on any day.");
            lines.Add($"Highest daily mean: {N(highest["mean"], glucose)} {unit} on {highest["date"]}.");
        }

        if (omitted.Count == 0 && rows.Count > 0)
        {
            var most = rows.MaxBy(r => (double)r["totalInsulin"]!)!;
            var least = rows.MinBy(r => (double)r["totalInsulin"]!)!;
            var carbs = rows.MaxBy(r => (double)r["totalCarbs"]!)!;
            lines.Add(Equals(least["totalInsulin"], most["totalInsulin"])
                ? $"Insulin by day: {N(most["totalInsulin"], "0.#")} U on every day."
                : $"Insulin by day: from {N(least["totalInsulin"], "0.#")} U on {least["date"]} to {N(most["totalInsulin"], "0.#")} U on {most["date"]}.");
            lines.Add($"Most carbs: {N(carbs["totalCarbs"], "0")} g on {carbs["date"]}.");
        }

        if (omitted.Count > 0)
            lines.Add($"Fields omitted: {string.Join(", ", omitted)} (scope not granted).");
        return string.Join('\n', lines);
    }
}
