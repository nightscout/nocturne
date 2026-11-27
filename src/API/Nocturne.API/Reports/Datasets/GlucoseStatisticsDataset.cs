using System.Globalization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;

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

    /// <summary>The field that says how much of the range the sensor covered, which a glucose figure's judgement states.</summary>
    internal const string CoverageField = "sensorWornPercent";

    private static readonly ReportFlagFact GlucoseFact = new(ReportFlagFactKind.Period, ReportRangeFamily.None, CoverageField);

    private static readonly ReportFlagFact ConsensusRangeFact = new(ReportFlagFactKind.Period, ReportRangeFamily.Consensus, CoverageField);

    private static readonly ReportFlagFact OwnRangeFact = new(ReportFlagFactKind.Period, ReportRangeFamily.Own, CoverageField);

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("timeInRangePercent", "Time in consensus range", ReportFieldKind.Percent, "%",
            "Percent of the covered time with glucose in the consensus target range.", Color: ChartColor.GlucoseInRange)
            { FlagFact = ConsensusRangeFact },
        new("timeBelow", "Time below consensus range", ReportFieldKind.Percent, "%",
            "Percent of the covered time with glucose below the consensus target range (low and very low).", Color: ChartColor.GlucoseLow)
            { FlagFact = ConsensusRangeFact },
        new("timeAbove", "Time above consensus range", ReportFieldKind.Percent, "%",
            "Percent of the covered time with glucose above the consensus target range (high and very high).", Color: ChartColor.GlucoseHigh)
            { FlagFact = ConsensusRangeFact },
        new("mean", "Mean glucose", ReportFieldKind.Number, ReportField.GlucoseUnit, "The mean glucose, in the display unit.")
            { FlagFact = GlucoseFact },
        new("gmi", "GMI", ReportFieldKind.Number, "%", "Glucose management indicator: the A1c the mean glucose predicts.")
            { FlagFact = GlucoseFact },
        new("cv", "CV", ReportFieldKind.Percent, "%", "Coefficient of variation: SD as a percent of the mean. 36% or less is the consensus target.")
            { FlagFact = GlucoseFact },
        new("sd", "SD", ReportFieldKind.Number, ReportField.GlucoseUnit, "Standard deviation of glucose, in the display unit.")
            { FlagFact = GlucoseFact },
        new("readings", "Readings", ReportFieldKind.Number, null, "The number of glucose readings in the range."),
        new("targetLow", "Range low", ReportFieldKind.Number, ReportField.GlucoseUnit,
            "The lower limit of the target range the time in range was measured against.", Setting: ReportSetting.FixedLimit),
        new("targetHigh", "Range high", ReportFieldKind.Number, ReportField.GlucoseUnit,
            "The upper limit of the target range the time in range was measured against.", Setting: ReportSetting.FixedLimit),
        new(CoverageField, "Sensor worn", ReportFieldKind.Percent, "%", "Percent of the range the sensor sent readings.")
            { FlagFact = ReportFlagFact.Period },
        new("timeInOwnRange", "Time in own range", ReportFieldKind.Percent, "%",
            "Percent of the covered time with glucose in the person's own target range schedule.", Scope.TherapyRead, ChartColor.GlucoseInRange)
            { FlagFact = OwnRangeFact },
        new("timeBelowOwnRange", "Time below own range", ReportFieldKind.Percent, "%",
            "Percent of the covered time with glucose below the person's own target range schedule.", Scope.TherapyRead, ChartColor.GlucoseLow)
            { FlagFact = OwnRangeFact },
        new("timeAboveOwnRange", "Time above own range", ReportFieldKind.Percent, "%",
            "Percent of the covered time with glucose above the person's own target range schedule.", Scope.TherapyRead, ChartColor.GlucoseHigh)
            { FlagFact = OwnRangeFact },
        new(OwnRange.LowField, "Own range low", ReportFieldKind.Number, ReportField.GlucoseUnit,
            "The lowest low of the person's own target range schedule.", Scope.TherapyRead, Setting: ReportSetting.Profile),
        new(OwnRange.HighField, "Own range high", ReportFieldKind.Number, ReportField.GlucoseUnit,
            "The highest high of the person's own target range schedule.", Scope.TherapyRead, Setting: ReportSetting.Profile),
        new(OwnRange.VariesField, "Own range varies", ReportFieldKind.Category, null,
            "yes when the person's own target range changes over the day, so its low and high are its widest edges.", Scope.TherapyRead),
        new("tdd", "Insulin per day", ReportFieldKind.Number, "U",
            "Total daily dose: bolus and basal insulin over the range, per day, as the Nocturne insulin delivery report computes it.",
            Scope.TreatmentsRead, ChartColor.InsulinBolus) { FlagFact = ReportFlagFact.Period },
        new("basalPercent", "Basal share", ReportFieldKind.Percent, "%", "Basal insulin as a percent of all insulin delivered.",
            Scope.TreatmentsRead, ChartColor.InsulinBasal) { FlagFact = ReportFlagFact.Period },
        new("carbsPerDay", "Carbs per day", ReportFieldKind.Number, "g", "Carbs entered over the range, per day.",
            Scope.TreatmentsRead, ChartColor.Carbs) { FlagFact = ReportFlagFact.Period },
    ];

    /// <summary>
    /// The person's own target range: the active profile's schedule as range analytics reads it
    /// (<see cref="ActiveTargetRange"/>), its widest edges in mg/dL, and the time spent against it.
    /// </summary>
    /// <param name="Time">The time below, in and above it, or null where it cannot be worked out (several entries and no time zone).</param>
    internal sealed record OwnRange(double Low, double High, bool Varies, PersonalRangeTimeInRange? Time)
    {
        public const string LowField = ReportFlagFact.OwnRangeLowField;
        public const string HighField = ReportFlagFact.OwnRangeHighField;
        public const string VariesField = ReportFlagFact.OwnRangeVariesField;
    }

    /// <summary>The fields read from insulin and carb records, left out without <see cref="Scope.TreatmentsRead"/>.</summary>
    internal static readonly IReadOnlyList<string> InsulinFields = ["tdd", "basalPercent", "carbsPerDay"];

    /// <summary>
    /// The fields measured against the person's target range schedule, read only for a read that names one of them
    /// (<see cref="ReportDatasetRequest.Fields"/>; a panel names its columns through <see cref="PanelShape"/>). Without
    /// <see cref="Scope.TherapyRead"/>, which <c>daily_profile</c> also takes for the target range, such a read leaves
    /// them out.
    /// </summary>
    internal static readonly IReadOnlyList<string> OwnRangeFields =
        ["timeInOwnRange", "timeBelowOwnRange", "timeAboveOwnRange", OwnRange.LowField, OwnRange.HighField, OwnRange.VariesField];

    public ReportReadShape PanelShape(IReadOnlyList<string> columns) =>
        columns.Where(OwnRangeFields.Contains).ToList() is { Count: > 0 } own ? new ReportReadShape(own) : ReportReadShape.Panel;

    public string? ReadVariant(ReportReadShape shape) => WantsOwnRange(shape.Fields) ? "own-range" : null;

    private static bool WantsOwnRange(IReadOnlyList<string>? fields) => fields?.Any(OwnRangeFields.Contains) == true;

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.StatTiles, ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [];

    public TimeSpan DefaultRange => TimeSpan.FromDays(14);

    public TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => 1;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var therapy = request.HasScope(Scope.TherapyRead);
        var ownRange = WantsOwnRange(request.Fields);
        var schedule = therapy && ownRange ? ScheduleAsync(request, cancellationToken) : null;
        var analysing = AnalyseAsync(request, cancellationToken);
        if (schedule is not null)
            await Task.WhenAll(analysing, schedule);
        var (entries, analysis) = await analysing;

        var useMmol = request.UseMmol;
        var count = analysis.BasicStats.Count;
        var percentages = analysis.TimeInRange.Percentages;
        var thresholds = analysis.TimeInRange.Thresholds ?? new GlycemicThresholds();
        double? IfAny(double value) => count == 0 ? null : value;

        var row = new Dictionary<string, object?>
        {
            ["timeInRangePercent"] = IfAny(ScaledRounding.Round(percentages.Target, 1)),
            ["timeBelow"] = IfAny(ScaledRounding.Round(percentages.VeryLow + percentages.Low, 1)),
            ["timeAbove"] = IfAny(ScaledRounding.Round(percentages.High + percentages.VeryHigh, 1)),
            ["mean"] = IfAny(ReportGlucose.Scale(analysis.BasicStats.Mean, useMmol)),
            ["gmi"] = IfAny(ScaledRounding.Round(analysis.GMI.Value, 1)),
            ["cv"] = analysis.GlycemicVariability is { } variability && count > 0
                ? ScaledRounding.Round(variability.CoefficientOfVariation, 1)
                : null,
            ["sd"] = IfAny(ReportGlucose.Scale(analysis.BasicStats.StandardDeviation, useMmol)),
            ["readings"] = count,
            ["targetLow"] = ReportGlucose.Scale(thresholds.TargetBottom, useMmol),
            ["targetHigh"] = ReportGlucose.Scale(thresholds.TargetTop, useMmol),
        };

        double? worn = count == 0 ? null : request.Services.GetRequiredService<IStatisticsService>()
            .CalculateCgmActivePercent(entries, request.From.UtcDateTime, request.To.UtcDateTime);
        row[CoverageField] = worn is { } w ? ScaledRounding.Round(w, 1) : null;

        if (schedule is not null)
        {
            var read = await schedule;
            var own = count == 0 ? null : await OwnRangeAsync(request, entries, read, cancellationToken);
            row["timeInOwnRange"] = own?.Time is { } inOwn ? ScaledRounding.Round(inOwn.InRangePercent, 1) : null;
            row["timeBelowOwnRange"] = own?.Time is { } belowOwn ? ScaledRounding.Round(belowOwn.BelowRangePercent, 1) : null;
            row["timeAboveOwnRange"] = own?.Time is { } aboveOwn ? ScaledRounding.Round(aboveOwn.AboveRangePercent, 1) : null;
            row[OwnRange.LowField] = own is null ? null : ReportGlucose.Scale(own.Low, useMmol);
            row[OwnRange.HighField] = own is null ? null : ReportGlucose.Scale(own.High, useMmol);
            row[OwnRange.VariesField] = own is null ? null : own.Varies ? "yes" : "no";
            if (own is not null)
                request.ReadLog.Record("profiles", 0);
        }

        var treatments = request.HasScope(Scope.TreatmentsRead);
        if (treatments)
        {
            var insulin = await InsulinAsync(request, cancellationToken);
            row["tdd"] = insulin is null ? null : ScaledRounding.Round(insulin.Tdd, 1);
            row["basalPercent"] = insulin is null || insulin.TotalInsulin <= 0 ? null : ScaledRounding.Round(insulin.BasalPercent, 1);
            row["carbsPerDay"] = insulin is null ? null : ScaledRounding.Round(insulin.CarbsPerDay, 0);
        }

        IReadOnlyList<ReportOmittedField> omitted =
        [
            .. treatments ? [] : InsulinFields.Select(f => new ReportOmittedField(f, Scope.TreatmentsRead)),
            .. therapy || !ownRange ? [] : OwnRangeFields.Select(f => new ReportOmittedField(f, Scope.TherapyRead)),
        ];

        return new ReportDatasetOutput
        {
            Rows = [row],
            Summary = Summarise(row, request.From, request.To, useMmol, [.. omitted.Select(o => o.Field)], request.Clock),
            Facts = FactsOf(row, useMmol),
            Sentence = SentenceOf(row, useMmol, treatments),
            Context = $"{ReportFacts.Window(request.From, request.To, request.Clock)}. {ReportFacts.Plural(count, "reading")}.",
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

    /// <summary>
    /// The target range schedule in force (<see cref="ActiveTargetRange"/>), or null when none is set or the store could
    /// not be read. A failed read empties only the own-range fields, as range analytics treats it.
    /// </summary>
    private static async Task<TargetRangeSchedule?> ScheduleAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var services = request.Services;
        try
        {
            return await ActiveTargetRange.ScheduleAsync(
                services.GetRequiredService<IActiveProfileResolver>(),
                services.GetRequiredService<ITargetRangeScheduleRepository>(),
                services.GetRequiredService<TimeProvider>(),
                cancellationToken);
        }
        catch (Exception ex) when (StoreFailure.Is(ex) || ex is TimeoutException)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger<GlucoseStatisticsDataset>()
                .LogWarning(ex, "Could not read the target range schedule; the own-range figures are left empty");
            return null;
        }
    }

    /// <summary>The person's own target range and the time against it over the readings, or null when no schedule is set.</summary>
    private static async Task<OwnRange?> OwnRangeAsync(
        ReportDatasetRequest request, IReadOnlyList<SensorGlucose> entries, TargetRangeSchedule? schedule, CancellationToken cancellationToken)
    {
        if (schedule is not { Entries.Count: > 0 })
            return null;

        var zone = (await request.Cache.ClockAsync(request, cancellationToken)).Zone;
        var time = request.Services.GetRequiredService<IStatisticsService>().CalculatePersonalRangeTime(entries, schedule.Entries, zone);
        return new OwnRange(
            schedule.Entries.Min(e => e.Low),
            schedule.Entries.Max(e => e.High),
            schedule.Entries.Select(e => (e.Low, e.High)).Distinct().Count() > 1,
            time);
    }

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
        IReadOnlyList<string>? omitted = null,
        ReportClock? clock = null)
    {
        var units = useMmol ? "mmol/L" : "mg/dL";
        string F(string key, string format) =>
            row.GetValueOrDefault(key) is IFormattable value ? value.ToString(format, CultureInfo.InvariantCulture) : "no data";
        var glucose = useMmol ? "0.0" : "0";

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Glucose statistics from {ReportFacts.Window(from, to, clock)}."),
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
            lines.Add($"Sensor worn {F(CoverageField, "0.#")}% of the period.");
            if (row.ContainsKey(OwnRange.LowField))
                lines.Add(row[OwnRange.LowField] is null
                    ? "Own target range: none set, or it could not be read."
                    : $"Own target range ({F(OwnRange.LowField, glucose)}-{F(OwnRange.HighField, glucose)} {units}"
                        + (row.GetValueOrDefault(OwnRange.VariesField) is "yes" ? ", its widest edges over the day" : "")
                        + $"): in {F("timeInOwnRange", "0.#")}%. Below: {F("timeBelowOwnRange", "0.#")}%. Above: {F("timeAboveOwnRange", "0.#")}%.");
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
