using System.Globalization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Mcp.Reports.Datasets;

/// <summary>
/// One row of glucose statistics over the range, computed as <c>GET /api/v4/statistics/range-analytics</c>
/// computes them: the canonical stream, <see cref="IStatisticsService.AnalyzeGlucoseDataExtended"/>,
/// the population's target range.
/// </summary>
internal sealed class GlucoseStatisticsDataset : IReportDataset
{
    private const DiabetesPopulation Population = DiabetesPopulation.Type1Adult;

    public string Name => GlucoseStatisticsParameters.Name;

    public string Description =>
        "Glucose statistics over the range, in one row: time in range, below range and above range "
        + "(percent of the time the readings cover), mean, standard deviation, coefficient of variation, "
        + "GMI, the number of readings and the target range the time in range was measured against. "
        + "The same figures as the Nocturne reports. Draw as stat_tiles or table.";

    public Type ParametersType => typeof(GlucoseStatisticsParameters);

    public string RequiredScope => Scope.GlucoseRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("timeInRangePercent", "Time in range", ReportFieldKind.Percent, "%",
            "Percent of the covered time with glucose in the target range.", Color: ChartColor.GlucoseInRange),
        new("timeBelow", "Time below range", ReportFieldKind.Percent, "%",
            "Percent of the covered time with glucose below the target range (low and very low).", Color: ChartColor.GlucoseLow),
        new("timeAbove", "Time above range", ReportFieldKind.Percent, "%",
            "Percent of the covered time with glucose above the target range (high and very high).", Color: ChartColor.GlucoseHigh),
        new("mean", "Mean glucose", ReportFieldKind.Number, ReportField.GlucoseUnit, "The mean glucose, in the display unit."),
        new("gmi", "GMI", ReportFieldKind.Number, "%", "Glucose management indicator: the A1c the mean glucose predicts."),
        new("cv", "CV", ReportFieldKind.Percent, "%", "Coefficient of variation: SD as a percent of the mean. 36% or less is the consensus target."),
        new("sd", "SD", ReportFieldKind.Number, ReportField.GlucoseUnit, "Standard deviation of glucose, in the display unit."),
        new("readings", "Readings", ReportFieldKind.Number, null, "The number of glucose readings in the range."),
        new("targetLow", "Range low", ReportFieldKind.Number, ReportField.GlucoseUnit,
            "The lower limit of the target range the time in range was measured against."),
        new("targetHigh", "Range high", ReportFieldKind.Number, ReportField.GlucoseUnit,
            "The upper limit of the target range the time in range was measured against."),
    ];

    public IReadOnlyList<ReportPrimitive> AllowedPrimitives { get; } = [ReportPrimitive.StatTiles, ReportPrimitive.Table];

    public TimeSpan DefaultRange => TimeSpan.FromDays(14);

    public TimeSpan MaxRange(ReportPrimitive primitive) => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => 1;

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> SampleRows { get; } =
    [
        new Dictionary<string, object?>
        {
            ["timeInRangePercent"] = 72.4, ["timeBelow"] = 2.1, ["timeAbove"] = 25.5, ["mean"] = 151.0,
            ["gmi"] = 6.9, ["cv"] = 33.2, ["sd"] = 50.0, ["readings"] = 4012, ["targetLow"] = 70.0, ["targetHigh"] = 180.0,
        },
    ];

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var services = request.Services;
        var from = request.From.UtcDateTime;
        var to = request.To.UtcDateTime;

        var raw = await services.GetRequiredService<ISensorGlucoseRepository>().GetForAnalyticsAsync(
            from, to, null, null, int.MaxValue, descending: false, ct: cancellationToken);
        var entries = await services.GetRequiredService<ICanonicalGlucoseService>().SelectAsync(raw.ToList(), cancellationToken);
        var clock = await services.GetRequiredService<TenantClockResolver>().ResolveAsync(cancellationToken);

        var analysis = services.GetRequiredService<IStatisticsService>().AnalyzeGlucoseDataExtended(
            entries, [], [], StatisticsService.WindowDays(from, to), Population, timeZone: clock.Zone);

        if (entries.Count > 0)
            request.ReadLog.Record("glucose", entries.Count);

        var useMmol = request.UseMmol;
        var count = analysis.BasicStats.Count;
        var percentages = analysis.TimeInRange.Percentages;
        var thresholds = analysis.TimeInRange.Thresholds ?? new GlycemicThresholds();
        double? IfAny(double value) => count == 0 ? null : value;

        var row = new Dictionary<string, object?>
        {
            ["timeInRangePercent"] = IfAny(Math.Round(percentages.Target, 1)),
            ["timeBelow"] = IfAny(Math.Round(percentages.VeryLow + percentages.Low, 1)),
            ["timeAbove"] = IfAny(Math.Round(percentages.High + percentages.VeryHigh, 1)),
            ["mean"] = IfAny(GlucoseUnits.Scale(analysis.BasicStats.Mean, useMmol)),
            ["gmi"] = IfAny(Math.Round(analysis.GMI.Value, 1)),
            ["cv"] = analysis.GlycemicVariability is { } variability && count > 0
                ? Math.Round(variability.CoefficientOfVariation, 1)
                : null,
            ["sd"] = IfAny(GlucoseUnits.Scale(analysis.BasicStats.StandardDeviation, useMmol)),
            ["readings"] = count,
            ["targetLow"] = GlucoseUnits.Scale(thresholds.TargetBottom, useMmol),
            ["targetHigh"] = GlucoseUnits.Scale(thresholds.TargetTop, useMmol),
        };

        return new ReportDatasetOutput
        {
            Rows = [row],
            Summary = Summarise(row, request.From, request.To, useMmol),
        };
    }

    internal static string Summarise(IReadOnlyDictionary<string, object?> row, DateTimeOffset from, DateTimeOffset to, bool useMmol)
    {
        var units = useMmol ? "mmol/L" : "mg/dL";
        string F(string key, string format) =>
            row[key] is IFormattable value ? value.ToString(format, CultureInfo.InvariantCulture) : "no data";
        var glucose = useMmol ? "0.0" : "0";

        var head = string.Create(CultureInfo.InvariantCulture,
            $"Glucose statistics from {from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm} UTC.");
        if (row["readings"] is 0)
            return head + "\nNo glucose readings in this period.";

        return string.Join('\n',
            head,
            $"Readings: {F("readings", "0")}.",
            $"Time in range ({F("targetLow", glucose)}-{F("targetHigh", glucose)} {units}): {F("timeInRangePercent", "0.#")}%. "
                + $"Below: {F("timeBelow", "0.#")}%. Above: {F("timeAbove", "0.#")}%.",
            $"Mean {F("mean", glucose)} {units}, SD {F("sd", glucose)} {units}, CV {F("cv", "0.#")}%.",
            $"GMI {F("gmi", "0.0")}%.");
    }
}
