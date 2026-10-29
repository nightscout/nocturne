using System.Globalization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using static Nocturne.API.Mcp.Reports.Datasets.LoopAnalysisDatasetRun;

namespace Nocturne.API.Mcp.Reports.Datasets;

/// <summary>
/// The hourly distribution the Glucose distribution report draws
/// (<c>ReportAnalysisResult.AveragedStats</c>, from <see cref="IStatisticsService.CalculateAveragedStats"/>
/// over the canonical stream in the tenant zone): one row per local hour with its percentiles and
/// the share of time in each glucose range.
/// </summary>
internal sealed class GlucoseDistributionByHourDataset : IReportDataset
{
    public string Name => GlucoseDistributionByHourParameters.Name;

    public string Description =>
        "Glucose by hour of day: one row per local hour (24 rows) with the glucose percentiles (P10, P25, P50, P75, P90) "
        + "across the range, the mean, the number of readings and days, and the percent of the hour's time very low, low, "
        + "in the tight range, above it, high and very high. The hourly distribution of the Nocturne glucose distribution "
        + "report. Draw as range_bands.";

    public Type ParametersType => typeof(GlucoseDistributionByHourParameters);

    public string RequiredScope => Scope.GlucoseRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("hour", "Hour", ReportFieldKind.TimeOfDay, "min", "Start of the local hour, minutes after midnight."),
        new("glucoseP10", "Glucose P10", ReportFieldKind.Number, ReportField.GlucoseUnit, "10th percentile of the hour's readings."),
        new("glucoseP25", "Glucose P25", ReportFieldKind.Number, ReportField.GlucoseUnit, "25th percentile of the hour's readings."),
        new("glucoseP50", "Glucose median", ReportFieldKind.Number, ReportField.GlucoseUnit, "Median of the hour's readings.", Color: ChartColor.GlucoseInRange),
        new("glucoseP75", "Glucose P75", ReportFieldKind.Number, ReportField.GlucoseUnit, "75th percentile of the hour's readings."),
        new("glucoseP90", "Glucose P90", ReportFieldKind.Number, ReportField.GlucoseUnit, "90th percentile of the hour's readings."),
        new("mean", "Mean", ReportFieldKind.Number, ReportField.GlucoseUnit, "Mean of the hour's readings."),
        new("readings", "Readings", ReportFieldKind.Number, null, "Readings in the hour across the range."),
        new("days", "Days", ReportFieldKind.Number, null, "Local days with a reading in the hour."),
        new("veryLow", "Very low", ReportFieldKind.Percent, "%", "Percent of the hour's covered time very low.", Color: ChartColor.GlucoseVeryLow),
        new("low", "Low", ReportFieldKind.Percent, "%", "Percent of the hour's covered time low.", Color: ChartColor.GlucoseLow),
        new("tightRange", "Tight range", ReportFieldKind.Percent, "%", "Percent of the hour's covered time in the tight range.", Color: ChartColor.GlucoseInRange),
        new("aboveTightRange", "Above tight range", ReportFieldKind.Percent, "%", "Percent of the hour's covered time above the tight range but in range."),
        new("high", "High", ReportFieldKind.Percent, "%", "Percent of the hour's covered time high.", Color: ChartColor.GlucoseHigh),
        new("veryHigh", "Very high", ReportFieldKind.Percent, "%", "Percent of the hour's covered time very high.", Color: ChartColor.GlucoseVeryHigh),
    ];

    public ReportBindings? DefaultBindings(ReportPrimitive primitive) => primitive == ReportPrimitive.RangeBands
        ? new ReportBindings
        {
            X = "hour",
            Y = ["glucoseP50"],
            Band = ["glucoseP10", "glucoseP25", "glucoseP75", "glucoseP90"],
        }
        : null;

    public IReadOnlyList<ReportPrimitive> AllowedPrimitives { get; } = [ReportPrimitive.RangeBands];

    public TimeSpan DefaultRange => TimeSpan.FromDays(14);

    public TimeSpan MaxRange(ReportPrimitive primitive) => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => 24;

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> SampleRows { get; } =
    [
        new Dictionary<string, object?>
        {
            ["hour"] = 420, ["glucoseP10"] = 92.0, ["glucoseP25"] = 108.0, ["glucoseP50"] = 128.0, ["glucoseP75"] = 155.0,
            ["glucoseP90"] = 184.0, ["mean"] = 133.0, ["readings"] = 168, ["days"] = 14, ["veryLow"] = 0.0, ["low"] = 1.2,
            ["tightRange"] = 61.0, ["aboveTightRange"] = 26.4, ["high"] = 10.1, ["veryHigh"] = 1.3,
        },
    ];

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var services = request.Services;
        var raw = await services.GetRequiredService<ISensorGlucoseRepository>().GetForAnalyticsAsync(
            request.From.UtcDateTime, request.To.UtcDateTime, null, null, int.MaxValue, descending: false, ct: cancellationToken);
        var entries = await services.GetRequiredService<ICanonicalGlucoseService>().SelectAsync(raw.ToList(), cancellationToken);
        var clock = await services.GetRequiredService<TenantClockResolver>().ResolveAsync(cancellationToken);
        var hours = services.GetRequiredService<IStatisticsService>().CalculateAveragedStats(entries, clock.Zone).ToList();
        if (entries.Count > 0)
            request.ReadLog.Record("glucose", entries.Count);

        var useMmol = request.UseMmol;
        var rows = hours.Select(h =>
        {
            var any = h.Count > 0;
            double? G(double mgdl) => any ? Glucose(mgdl, useMmol) : null;
            double? P(double percent) => any ? percent : null;
            return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["hour"] = h.Hour * 60,
                ["glucoseP10"] = G(h.Percentiles.P10),
                ["glucoseP25"] = G(h.Percentiles.P25),
                ["glucoseP50"] = G(h.Percentiles.P50),
                ["glucoseP75"] = G(h.Percentiles.P75),
                ["glucoseP90"] = G(h.Percentiles.P90),
                ["mean"] = G(h.Mean),
                ["readings"] = h.Count,
                ["days"] = h.DayCount,
                ["veryLow"] = P(h.TimeInRange.VeryLow),
                ["low"] = P(h.TimeInRange.Low),
                ["tightRange"] = P(h.TimeInRange.TightTarget),
                ["aboveTightRange"] = P(h.TimeInRange.AboveTightTarget),
                ["high"] = P(h.TimeInRange.High),
                ["veryHigh"] = P(h.TimeInRange.VeryHigh),
            };
        }).ToList();

        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = Summarise(hours, request.From, request.To, clock.Zone is null, useMmol),
        };
    }

    internal static string Summarise(IReadOnlyList<AveragedStats> hours, DateTimeOffset from, DateTimeOffset to, bool utc, bool useMmol)
    {
        var head = string.Create(CultureInfo.InvariantCulture,
            $"Glucose by hour of day from {from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm} UTC{(utc ? ", hours in UTC: the tenant has no time zone set." : ", hours in the tenant's time zone.")}");
        var withData = hours.Where(h => h.Count > 0).ToList();
        if (withData.Count == 0)
            return head + "\nNo glucose readings in this period.";

        var unit = useMmol ? "mmol/L" : "mg/dL";
        string G(double mgdl) => Number(Glucose(mgdl, useMmol)!.Value, useMmol ? "0.0" : "0");
        string H(int hour) => Clock(hour * 60);
        var highest = withData.MaxBy(h => h.Percentiles.P50)!;
        var lowest = withData.MinBy(h => h.Percentiles.P50)!;
        var mostBelow = withData.MaxBy(h => h.TimeInRange.VeryLow + h.TimeInRange.Low)!;
        var mostAbove = withData.MaxBy(h => h.TimeInRange.High + h.TimeInRange.VeryHigh)!;

        var lines = new List<string>
        {
            head,
            $"Readings: {withData.Sum(h => h.Count)}, in {withData.Count} of 24 hours.",
            $"Median glucose is highest in the hour from {H(highest.Hour)} ({G(highest.Percentiles.P50)} {unit}) and lowest from {H(lowest.Hour)} ({G(lowest.Percentiles.P50)} {unit}).",
            $"Most time above range: the hour from {H(mostAbove.Hour)} ({Number(mostAbove.TimeInRange.High + mostAbove.TimeInRange.VeryHigh, "0.#")}%).",
        };
        var below = mostBelow.TimeInRange.VeryLow + mostBelow.TimeInRange.Low;
        lines.Add(below > 0
            ? $"Most time below range: the hour from {H(mostBelow.Hour)} ({Number(below, "0.#")}%)."
            : "No time below range in any hour.");
        return string.Join('\n', lines);
    }
}
