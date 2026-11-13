using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using static Nocturne.API.Reports.Datasets.LoopAnalysisDatasetRun;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// The Daily profile page (<c>GET /api/v4/loop-analysis/daily-profile</c>) as a dataset: one row
/// per local five-minute slot of the day, every series as the page computes and narrows it.
/// </summary>
internal sealed class DailyProfileDataset : IReportDataset
{
    public string Name => DailyProfileParameters.Name;

    public string Label => "Daily profile";

    public string Description => "Glucose percentiles for each five minutes of the day, with basal, insulin on board and carbs on board.";

    public Type ParametersType => typeof(DailyProfileParameters);

    public string RequiredScope => Scope.GlucoseRead;

    private static readonly Dictionary<string, LoopAnalysisSeries> FieldSeries = new()
    {
        ["glucoseP10"] = LoopAnalysisSeries.Glucose,
        ["glucoseP25"] = LoopAnalysisSeries.Glucose,
        ["glucoseP50"] = LoopAnalysisSeries.Glucose,
        ["glucoseP75"] = LoopAnalysisSeries.Glucose,
        ["glucoseP90"] = LoopAnalysisSeries.Glucose,
        ["readings"] = LoopAnalysisSeries.Glucose,
        ["scheduledBasal"] = LoopAnalysisSeries.ScheduledBasal,
        ["deliveredBasal"] = LoopAnalysisSeries.DeliveredBasal,
        ["netLoopCorrection"] = LoopAnalysisSeries.NetLoopCorrection,
        ["iob"] = LoopAnalysisSeries.Iob,
        ["cob"] = LoopAnalysisSeries.Cob,
        ["targetLow"] = LoopAnalysisSeries.Target,
        ["targetHigh"] = LoopAnalysisSeries.Target,
    };

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("minuteOfDay", "Time of day", ReportFieldKind.TimeOfDay, "min", "Start of the slot, minutes after local midnight."),
        new("glucoseP10", "Glucose P10", ReportFieldKind.Number, ReportField.GlucoseUnit, "10th percentile of the slot's readings, pooled across days.", Color: ChartColor.GlucoseInRange, Aggregate: ReportAggregate.Mean),
        new("glucoseP25", "Glucose P25", ReportFieldKind.Number, ReportField.GlucoseUnit, "25th percentile of the slot's readings.", Color: ChartColor.GlucoseInRange, Aggregate: ReportAggregate.Mean),
        new("glucoseP50", "Glucose median", ReportFieldKind.Number, ReportField.GlucoseUnit, "Median of the slot's readings.",
            Color: ChartColor.GlucoseInRange, GlucoseScale: true, Aggregate: ReportAggregate.Mean),
        new("glucoseP75", "Glucose P75", ReportFieldKind.Number, ReportField.GlucoseUnit, "75th percentile of the slot's readings.", Color: ChartColor.GlucoseInRange, Aggregate: ReportAggregate.Mean),
        new("glucoseP90", "Glucose P90", ReportFieldKind.Number, ReportField.GlucoseUnit, "90th percentile of the slot's readings.", Color: ChartColor.GlucoseInRange, Aggregate: ReportAggregate.Mean),
        new("readings", "Readings", ReportFieldKind.Number, null, "Readings in the slot across the range.", Aggregate: ReportAggregate.Sum),
        new("scheduledBasal", "Scheduled basal", ReportFieldKind.Number, "U/h", "Median scheduled basal, including any override's scale.",
            Scope.TherapyRead, ChartColor.InsulinScheduledBasal, Aggregate: ReportAggregate.Mean, Setting: ReportSetting.Profile),
        new("deliveredBasal", "Delivered basal", ReportFieldKind.Number, "U/h", "Median delivered basal.", Scope.TreatmentsRead, ChartColor.InsulinBasal, Aggregate: ReportAggregate.Mean),
        new("netLoopCorrection", "Net loop correction", ReportFieldKind.Number, "U/h",
            "Mean across days of the algorithm's temp basal above schedule plus its automatic boluses.", Scope.TreatmentsRead, Aggregate: ReportAggregate.Mean,
            Setting: ReportSetting.ProfileRelative),
        new("iob", "IOB median", ReportFieldKind.Number, "U", "Median insulin on board.", Scope.TreatmentsRead, ChartColor.InsulinBolus, Aggregate: ReportAggregate.Mean),
        new("cob", "COB median", ReportFieldKind.Number, "g", "Median carbs on board.", Scope.TreatmentsRead, ChartColor.Carbs, Aggregate: ReportAggregate.Mean),
        new("targetLow", "Target low", ReportFieldKind.Number, ReportField.GlucoseUnit,
            "The low edge of the target range in force (a temporary target where one ran), median across days.", Scope.TherapyRead, ChartColor.GlucoseLow, Aggregate: ReportAggregate.Mean, Setting: ReportSetting.Profile),
        new("targetHigh", "Target high", ReportFieldKind.Number, ReportField.GlucoseUnit,
            "The high edge of the target range in force, median across days.", Scope.TherapyRead, ChartColor.GlucoseHigh, Aggregate: ReportAggregate.Mean, Setting: ReportSetting.Profile),
        new(ReportField.SentenceField, "Sentence", ReportFieldKind.Category, null,
            "One sentence that states the slot's glucose figures; the chart shows it for the slot under the pointer.", Companion: true),
    ];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) => mark switch
    {
        ReportMark.Band => new ReportLayerBindings
        {
            X = "minuteOfDay",
            Y = ["glucoseP50"],
            Edges = ["glucoseP10", "glucoseP25", "glucoseP75", "glucoseP90"],
        },
        ReportMark.Line or ReportMark.Area => new ReportLayerBindings { X = "minuteOfDay", Y = ["glucoseP50"] },
        _ => null,
    };

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Band, ReportMark.Line, ReportMark.Area];

    public TimeSpan DefaultRange => TimeSpan.FromDays(14);

    public TimeSpan MaxRange => LoopAnalysisDatasetRun.MaxRange;

    public int PointBudget => 288;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters)
    {
        var p = (DailyProfileParameters)parameters;
        if (p.ProfileName is { Length: > LoopAnalysisDatasetParameters.MaxProfileNameLength })
            yield return ("profileName", $"Use {LoopAnalysisDatasetParameters.MaxProfileNameLength} characters or fewer.");
        if (p.DayOfWeek is { } days && days.Distinct().Count() != days.Count)
            yield return ("dayOfWeek", "A weekday is named twice.");
        foreach (var error in p.Days?.Validate("days") ?? [])
            yield return error;
    }

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var parameters = (DailyProfileParameters)request.Parameters;
        var dayFilter = parameters.Days;
        if (dayFilter is { NeedsTreatments: true } && !request.HasScope(Scope.TreatmentsRead))
            return DayFilterWithheld("Daily profile", dayFilter);
        var resolved = await ResolveAsync(request, parameters, null, cancellationToken);
        var service = request.Services.GetRequiredService<ILoopAnalysisService>();
        var report = resolved.Filter is { } filter && resolved.Zone is { } zone
            ? dayFilter is null
                ? await service.GetDailyProfileAsync(filter, zone, cancellationToken)
                : await service.GetDailyProfileForDaysAsync(filter, zone, dayFilter.ToCondition(), cancellationToken)
            : new DailyProfileResponse();
        report = Serve(resolved, report, request);

        var useMmol = request.UseMmol;
        var rows = report.Primary.Slots.Select(s => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["minuteOfDay"] = s.MinuteOfDay,
            ["glucoseP10"] = Glucose(s.Glucose?.P10, useMmol),
            ["glucoseP25"] = Glucose(s.Glucose?.P25, useMmol),
            ["glucoseP50"] = Glucose(s.Glucose?.P50, useMmol),
            ["glucoseP75"] = Glucose(s.Glucose?.P75, useMmol),
            ["glucoseP90"] = Glucose(s.Glucose?.P90, useMmol),
            ["readings"] = s.Glucose?.N ?? 0,
            ["scheduledBasal"] = Round(s.ScheduledBasal?.P50, 3),
            ["deliveredBasal"] = Round(s.DeliveredBasal?.P50, 3),
            ["netLoopCorrection"] = Round(s.NetLoopCorrection?.Mean, 3),
            ["iob"] = Round(s.Iob?.P50),
            ["cob"] = Round(s.Cob?.P50, 1),
            ["targetLow"] = Glucose(s.TargetLow, useMmol),
            ["targetHigh"] = Glucose(s.TargetHigh, useMmol),
            [ReportField.SentenceField] = $"At {Clock(s.MinuteOfDay)}: {EventResponseDataset.GlucoseSpread(s.Glucose, s.Glucose?.N ?? 0, "reading", useMmol)}",
        }).ToList();

        var readings = report.Primary.Slots.Sum(s => s.Glucose?.N ?? 0);
        if (readings > 0)
            request.ReadLog.Record("glucose", readings);
        if (dayFilter is not null && report.Primary.DaySelection is not null)
            request.ReadLog.Record(DayFilterCategory(dayFilter), 0);
        if (report.Primary.Days.Count > 0)
        {
            // Categories by the series served; their slot values are aggregates, so no count is given.
            bool Served(params LoopAnalysisSeries[] series) => series.Any(s => !report.WithheldSeries.Contains(s));
            if (Served(LoopAnalysisSeries.DeliveredBasal, LoopAnalysisSeries.NetLoopCorrection, LoopAnalysisSeries.Iob, LoopAnalysisSeries.Cob))
                request.ReadLog.Record("treatments", 0);
            if (Served(LoopAnalysisSeries.ScheduledBasal, LoopAnalysisSeries.Target))
                request.ReadLog.Record("profiles", 0);
        }

        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = dayFilter is null ? Summarise(report, useMmol) : $"{dayFilter.Summary(report.Primary.DaySelection)}\n{Summarise(report, useMmol)}",
            Facts = FactsOf(report, useMmol),
            Sentence = NoTimeZone(report, "Daily profile") ?? SentenceOf(report),
            SentenceAbout = NoTimeZone(report, "Daily profile") is null ? SentenceAboutOf(report) : null,
            OtherSentences = NoTimeZone(report, "Daily profile") is null ? [new ReportSentence(PooledSentence, GlucoseFields)] : [],
            SentenceComparesSettings = NoTimeZone(report, "Daily profile") is null && report.Primary.Summary.DeliveredToScheduledRatio is not null
                && report.Primary.Slots.Any(s => s.Glucose is not null),
            Context = dayFilter is null
                ? ContextLine(report, ReportFacts.Plural(readings, "reading"))
                : $"{dayFilter.Summary(report.Primary.DaySelection)} {ContextLine(report, ReportFacts.Plural(readings, "reading"))}",
            Omitted = Omitted(report, FieldSeries),
            Window = Window(report),
            FieldLabels = DayFieldLabels(Fields, dayFilter),
            Name = ReportNames.Of(parameters),
            Days = report.Primary.DaySelection is { } selection ? ReportDayCounts.Of(selection) : null,
        };
    }

    /// <summary>Each number field's label with the days kept, so two layers of one dataset read apart: "Glucose median, days with exercise".</summary>
    internal static IReadOnlyDictionary<string, string>? DayFieldLabels(IEnumerable<ReportField> fields, ReportDayFilter? days) =>
        days is null ? null : fields.Where(f => f.IsNumber && !f.Companion).ToDictionary(f => f.Name, f => $"{f.Label}, {days.Words()}");

    /// <summary>
    /// The read-audit category that decided which days a filter kept, whether or not it kept any: the spans for
    /// exercise and illness, the carb records for carbs, the readings for lows.
    /// </summary>
    internal static string DayFilterCategory(ReportDayFilter days) => days.Kind switch
    {
        ReportDayKind.Exercise or ReportDayKind.Illness => "state_spans",
        ReportDayKind.Carbs => "treatments",
        _ => "glucose",
    };

    /// <summary>The panel of a day filter the token cannot read: no data, never every day.</summary>
    internal static ReportDatasetOutput DayFilterWithheld(string what, ReportDayFilter days)
    {
        var text = $"{what}: not shown. Keeping only {days.Words()} needs the treatments.read scope, which this token does not hold.";
        return new ReportDatasetOutput { Rows = [], Summary = text, Sentence = text };
    }

    /// <summary>Days with data, the highest and lowest median and the widest spread by time of day, and the insulin figures served.</summary>
    internal static IReadOnlyList<ReportFact> FactsOf(DailyProfileResponse report, bool useMmol)
    {
        if (report.TimeZoneUnavailableReason is not null)
            return [];
        var range = report.Primary;
        var facts = new List<ReportFact> { ReportFacts.Count("Days with data", range.Summary.DaysWithData).Of(GlucoseFields) };
        var withGlucose = range.Slots.Where(s => s.Glucose is not null).ToList();
        if (withGlucose.Count > 0)
        {
            var highest = withGlucose.MaxBy(s => s.Glucose!.P50)!;
            var lowest = withGlucose.MinBy(s => s.Glucose!.P50)!;
            var widest = withGlucose.MaxBy(s => s.Glucose!.P90 - s.Glucose!.P10)!;
            facts.Add(ReportFacts.Glucose("Highest median", highest.Glucose!.P50, useMmol, $"at {Clock(highest.MinuteOfDay)}").Of(GlucoseFields));
            facts.Add(ReportFacts.Glucose("Lowest median", lowest.Glucose!.P50, useMmol, $"at {Clock(lowest.MinuteOfDay)}").Of(GlucoseFields));
            facts.Add(new ReportFact("Widest P10 to P90",
                $"{GlucoseSummary.Format(widest.Glucose!.P10, useMmol)}-{GlucoseSummary.Format(widest.Glucose!.P90, useMmol)}",
                ReportFacts.GlucoseUnit(useMmol), $"at {Clock(widest.MinuteOfDay)}", GlucoseFields));
        }
        if (range.Summary.Insulin is { } insulin)
            facts.Add(new ReportFact("Insulin per day", Number(insulin.Total.P50, "0.#"), "U", $"median of {insulin.Total.N} days",
                ["deliveredBasal|netLoopCorrection|iob"]));
        if (range.Summary.NetLoopCorrectionPerDay is { } perDay)
            facts.Add(new ReportFact("Net loop correction", Number(perDay, "0.##"), "U/day", About: ["netLoopCorrection"]));
        return facts;
    }

    private const string PooledSentence = "Glucose percentiles in each five-minute slot of the day, pooled across the days.";

    private static readonly string[] GlucoseFields = ["glucoseP10|glucoseP25|glucoseP50|glucoseP75|glucoseP90"];

    /// <summary>The fields <see cref="SentenceOf"/> is about, as it chose its sentence.</summary>
    public IReadOnlyList<string>? RowSentenceAbout => GlucoseFields;

    internal static IReadOnlyList<string>? SentenceAboutOf(DailyProfileResponse report) =>
        !report.Primary.Slots.Any(s => s.Glucose is not null) ? null
        : report.Primary.Summary.DeliveredToScheduledRatio is not null ? ["deliveredBasal", "scheduledBasal"]
        : GlucoseFields;

    internal static string SentenceOf(DailyProfileResponse report)
    {
        if (!report.Primary.Slots.Any(s => s.Glucose is not null))
            return "No glucose readings in the analyzed time.";
        return report.Primary.Summary.DeliveredToScheduledRatio is { } ratio
            ? $"Delivered basal was {Number(ratio, "0.##")} times the scheduled basal."
            : PooledSentence;
    }

    internal static string Summarise(DailyProfileResponse report, bool useMmol)
    {
        const string what = "Daily profile";
        if (NoTimeZone(report, what) is { } noZone)
            return noZone;

        var lines = new List<string>(Context(report, what));
        var range = report.Primary;
        var summary = range.Summary;
        lines.Add($"Days with data: {summary.DaysWithData}.");

        var unit = useMmol ? "mmol/L" : "mg/dL";
        string G(double mgdl) => Number(Glucose(mgdl, useMmol)!.Value, useMmol ? "0.0" : "0");
        var withGlucose = range.Slots.Where(s => s.Glucose is not null).ToList();
        if (withGlucose.Count > 0)
        {
            var highest = withGlucose.MaxBy(s => s.Glucose!.P50)!;
            var lowest = withGlucose.MinBy(s => s.Glucose!.P50)!;
            lines.Add($"Median glucose is highest at {Clock(highest.MinuteOfDay)} ({G(highest.Glucose!.P50)} {unit}) "
                + $"and lowest at {Clock(lowest.MinuteOfDay)} ({G(lowest.Glucose!.P50)} {unit}).");
            var widest = withGlucose.MaxBy(s => s.Glucose!.P90 - s.Glucose!.P10)!;
            lines.Add($"The spread from P10 to P90 is widest at {Clock(widest.MinuteOfDay)}: {G(widest.Glucose!.P10)} to {G(widest.Glucose!.P90)} {unit}.");
        }
        else
        {
            lines.Add("No glucose readings in the analyzed time.");
        }

        if (summary.Insulin is { } insulin)
            lines.Add($"Total insulin per day: median {Number(insulin.Total.P50, "0.#")} U over {insulin.Total.N} days.");
        if (summary.NetLoopCorrectionPerDay is { } perDay)
            lines.Add($"Net loop correction: {Number(perDay, "0.##")} U per day"
                + (summary.NetLoopCorrectionTddPercent is { } share ? $", {Number(share, "0.#")}% of total insulin." : "."));
        if (summary.DeliveredToScheduledRatio is { } ratio)
            lines.Add($"Delivered basal was {Number(ratio, "0.##")} times the scheduled basal.");
        return string.Join('\n', lines);
    }
}
