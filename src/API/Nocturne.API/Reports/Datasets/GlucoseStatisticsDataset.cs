using System.Globalization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// One row of glucose statistics over the range, computed as <c>GET /api/v4/statistics/range-analytics</c>
/// computes them: the canonical stream, <see cref="IStatisticsService.AnalyzeGlucoseDataExtended"/>,
/// the population's target range. With treatments.read, the insulin and carbs per day as
/// <c>GET /api/v4/statistics/insulin-delivery-stats</c> computes them.
/// </summary>
internal sealed class GlucoseStatisticsDataset : IReportDataset
{
    public string Name => GlucoseStatisticsParameters.Name;

    public string Label => "Glucose statistics";

    public string Description => "Time in range, mean, GMI, CV and the other glucose figures of the range.";

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
        new("tdd", "Insulin per day", ReportFieldKind.Number, "U",
            "Total daily dose: bolus and basal insulin over the range, per day, as the Nocturne insulin delivery report computes it.",
            Scope.TreatmentsRead, ChartColor.InsulinBolus),
        new("basalPercent", "Basal share", ReportFieldKind.Percent, "%", "Basal insulin as a percent of all insulin delivered.",
            Scope.TreatmentsRead, ChartColor.InsulinBasal),
        new("carbsPerDay", "Carbs per day", ReportFieldKind.Number, "g", "Carbs entered over the range, per day.",
            Scope.TreatmentsRead, ChartColor.Carbs),
    ];

    /// <summary>The fields read from insulin and carb records, left out without <see cref="Scope.TreatmentsRead"/>.</summary>
    internal static readonly IReadOnlyList<string> InsulinFields = ["tdd", "basalPercent", "carbsPerDay"];

    public IReadOnlyList<ReportPrimitive> AllowedPrimitives { get; } = [ReportPrimitive.StatTiles, ReportPrimitive.Table];

    public TimeSpan DefaultRange => TimeSpan.FromDays(14);

    public TimeSpan MaxRange(ReportPrimitive primitive) => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => 1;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var (_, analysis) = await AnalyseAsync(request, cancellationToken);

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

        var treatments = request.HasScope(Scope.TreatmentsRead);
        if (treatments)
        {
            var insulin = await InsulinAsync(request, cancellationToken);
            row["tdd"] = insulin is null ? null : Math.Round(insulin.Tdd, 1);
            row["basalPercent"] = insulin is null || insulin.TotalInsulin <= 0 ? null : Math.Round(insulin.BasalPercent, 1);
            row["carbsPerDay"] = insulin is null ? null : Math.Round(insulin.CarbsPerDay, 0);
        }

        IReadOnlyList<ReportOmittedField> omitted = treatments
            ? []
            : [.. InsulinFields.Select(f => new ReportOmittedField(f, Scope.TreatmentsRead))];

        return new ReportDatasetOutput
        {
            Rows = [row],
            Summary = Summarise(row, request.From, request.To, useMmol, [.. omitted.Select(o => o.Field)]),
            Facts = FactsOf(row, useMmol),
            Sentence = SentenceOf(row, useMmol, treatments),
            Context = $"{ReportFacts.Window(request.From, request.To, request.Zone)}. {ReportFacts.Plural(count, "reading")}.",
            Omitted = omitted,
        };
    }

    /// <summary>
    /// The canonical readings over the request's range and their analysis, as
    /// <c>GET /api/v4/statistics/range-analytics</c> computes it, shared by the run's panels over the
    /// same range (<see cref="ReportRunCache"/>), which records the readings for the audit once.
    /// </summary>
    internal static async Task<(IReadOnlyList<SensorGlucose> Entries, ExtendedGlucoseAnalytics Analysis)> AnalyseAsync(
        ReportDatasetRequest request, CancellationToken cancellationToken) =>
        (await request.Cache.GlucoseAsync(request, cancellationToken), await request.Cache.AnalysisAsync(request, cancellationToken));

    /// <summary>The insulin delivery figures over the range, or null when no insulin or carb record is in it.</summary>
    internal static async Task<InsulinDeliveryStatistics?> InsulinAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var from = request.From.UtcDateTime;
        var to = request.To.UtcDateTime;
        var records = await InsulinRecords.ReadAsync(request.Services, from, to, cancellationToken);
        if (records.Count == 0)
            return null;

        request.ReadLog.Record("treatments", records.Count);
        var now = request.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        return request.Services.GetRequiredService<IStatisticsService>().CalculateInsulinDeliveryStatistics(
            records.ManualBoluses, records.AlgorithmBoluses, records.TempBasals, records.Carbs,
            from, to, StatisticsService.UntilMills(to, now), records.BasalInjections);
    }

    /// <summary>Time in range, below, above, mean, GMI and CV, where the range has readings.</summary>
    internal static IReadOnlyList<ReportFact> FactsOf(IReadOnlyDictionary<string, object?> row, bool useMmol)
    {
        if (row["readings"] is 0)
            return [];
        var glucose = useMmol ? "0.0" : "0";
        string? F(string key, string format) => row.GetValueOrDefault(key) is double d ? ReportFacts.Number(d, format) : null;
        var facts = new List<ReportFact>
        {
            new("In range", F("timeInRangePercent", "0.#") ?? "–", "%", $"{F("targetLow", glucose)}-{F("targetHigh", glucose)} {ReportFacts.GlucoseUnit(useMmol)}"),
            new("Below range", F("timeBelow", "0.#") ?? "–", "%"),
            new("Above range", F("timeAbove", "0.#") ?? "–", "%"),
            new("Mean", F("mean", glucose) ?? "–", ReportFacts.GlucoseUnit(useMmol)),
            new("GMI", F("gmi", "0.0") ?? "–", "%"),
        };
        if (F("cv", "0.#") is { } cv)
            facts.Add(new ReportFact("CV", cv, "%"));
        return facts;
    }

    /// <summary>The spread, and with treatments.read the insulin and carbs per day.</summary>
    internal static string SentenceOf(IReadOnlyDictionary<string, object?> row, bool useMmol, bool treatments)
    {
        if (row["readings"] is 0)
            return "No glucose readings in this period.";
        var unit = ReportFacts.GlucoseUnit(useMmol);
        var sd = row.GetValueOrDefault("sd") is double s ? ReportFacts.Number(s, useMmol ? "0.0" : "0") : "–";
        var text = $"Standard deviation {sd} {unit}.";
        if (treatments)
            text += row.GetValueOrDefault("tdd") is double tdd
                ? $" Insulin per day {ReportFacts.Number(tdd)} U"
                    + (row.GetValueOrDefault("basalPercent") is double basal ? $", {ReportFacts.Number(basal, "0")}% of it basal" : "")
                    + (row.GetValueOrDefault("carbsPerDay") is double carbs ? $"; carbs per day {ReportFacts.Number(carbs, "0")} g." : ".")
                : " No insulin or carb records in this period.";
        return text;
    }

    internal static string Summarise(
        IReadOnlyDictionary<string, object?> row,
        DateTimeOffset from,
        DateTimeOffset to,
        bool useMmol,
        IReadOnlyList<string>? omitted = null)
    {
        var units = useMmol ? "mmol/L" : "mg/dL";
        string F(string key, string format) =>
            row.GetValueOrDefault(key) is IFormattable value ? value.ToString(format, CultureInfo.InvariantCulture) : "no data";
        var glucose = useMmol ? "0.0" : "0";

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Glucose statistics from {from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm} UTC."),
        };
        if (row["readings"] is 0)
        {
            lines.Add("No glucose readings in this period.");
        }
        else
        {
            lines.Add($"Readings: {F("readings", "0")}.");
            lines.Add($"Time in range ({F("targetLow", glucose)}-{F("targetHigh", glucose)} {units}): {F("timeInRangePercent", "0.#")}%. "
                + $"Below: {F("timeBelow", "0.#")}%. Above: {F("timeAbove", "0.#")}%.");
            lines.Add($"Mean {F("mean", glucose)} {units}, SD {F("sd", glucose)} {units}, CV {F("cv", "0.#")}%.");
            lines.Add($"GMI {F("gmi", "0.0")}%.");
        }

        if (row.ContainsKey("tdd"))
            lines.Add(row["tdd"] is null
                ? "Insulin and carbs: no records in this period."
                : $"Insulin per day {F("tdd", "0.#")} U, {F("basalPercent", "0")}% of it basal. Carbs per day {F("carbsPerDay", "0")} g.");
        if (omitted is { Count: > 0 })
            lines.Add($"Fields omitted: {string.Join(", ", omitted)} (scope not granted).");
        return string.Join('\n', lines);
    }
}
