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
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Contracts.Repositories;

namespace Nocturne.API.Reports.Datasets;

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

    public string Label => "Daily summary";

    public string Description => "One row for each day: time in range, mean glucose, insulin and carbs.";

    public Type ParametersType => typeof(DailySummaryParameters);

    public string RequiredScope => Scope.GlucoseRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("date", "Date", ReportFieldKind.Category, null, "The local date, yyyy-MM-dd."),
        new("day", "Day", ReportFieldKind.Day, null, "Local midnight at the start of the day, Unix milliseconds."),
        new("readings", "Readings", ReportFieldKind.Number, null, "The day's glucose readings.", Aggregate: ReportAggregate.Sum),
        new("timeInRange", "In range", ReportFieldKind.Percent, "%", "Percent of the day's covered time in range.", Color: ChartColor.GlucoseInRange, Aggregate: ReportAggregate.Mean),
        new("timeBelow", "Below range", ReportFieldKind.Percent, "%", "Percent of the day's covered time below range (low and very low).", Color: ChartColor.GlucoseLow, Aggregate: ReportAggregate.Mean),
        new("timeAbove", "Above range", ReportFieldKind.Percent, "%", "Percent of the day's covered time above range (high and very high).", Color: ChartColor.GlucoseHigh, Aggregate: ReportAggregate.Mean),
        new("mean", "Mean glucose", ReportFieldKind.Number, ReportField.GlucoseUnit, "The mean of the day's readings.", Color: ChartColor.GlucoseInRange, Aggregate: ReportAggregate.Mean),
        new("totalInsulin", "Insulin", ReportFieldKind.Number, "U", "The day's insulin, bolus and basal.", Scope.TreatmentsRead, ChartColor.InsulinBolus, Aggregate: ReportAggregate.Sum),
        new("totalBolus", "Bolus", ReportFieldKind.Number, "U", "The day's bolus insulin, the algorithm's included.", Scope.TreatmentsRead, ChartColor.InsulinBolus, Aggregate: ReportAggregate.Sum),
        new("totalBasal", "Basal", ReportFieldKind.Number, "U", "The day's basal insulin.", Scope.TreatmentsRead, ChartColor.InsulinBasal, Aggregate: ReportAggregate.Sum),
        new("totalCarbs", "Carbs", ReportFieldKind.Number, "g", "The day's carbs.", Scope.TreatmentsRead, ChartColor.Carbs, Aggregate: ReportAggregate.Sum),
        new("partial", "Partial day", ReportFieldKind.Category, null,
            "true for a day that starts before the last 24 hours a history-limited token reads: its row covers only part of the day."),
    ];

    /// <summary>The fields read from insulin and carb records.</summary>
    internal static readonly IReadOnlyList<string> TreatmentFields = ["totalInsulin", "totalBolus", "totalBasal", "totalCarbs"];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) => mark switch
    {
        ReportMark.Bars => new ReportLayerBindings { X = "day", Y = ["timeBelow", "timeInRange", "timeAbove"], Stacked = true },
        ReportMark.Line or ReportMark.Points => new ReportLayerBindings { X = "day", Y = ["timeInRange"] },
        _ => null,
    };

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Bars, ReportMark.Line, ReportMark.Points];

    public TimeSpan DefaultRange => TimeSpan.FromDays(14);

    public TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => V4ReadLimits.MaxAnalyticsSpanDays + 1;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) =>
        ((DailySummaryParameters)parameters).Days?.Validate("days") ?? [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var services = request.Services;
        var treatments = request.HasScope(Scope.TreatmentsRead);
        var dayFilter = ((DailySummaryParameters)request.Parameters).Days;
        if (dayFilter is { NeedsTreatments: true } && !treatments)
            return DailyProfileDataset.DayFilterWithheld("Daily summary", dayFilter);
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
        DaySelection? selection = null;
        if (dayFilter is not null && days.Count > 0)
        {
            selection = await SelectDaysAsync(dayFilter, days, glucose, records, zone, request, cancellationToken);
            var kept = selection.Kept.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToHashSet();
            days = [.. days.Where(d => kept.Contains(d.Date))];
        }
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
                ["partial"] = request.HistoryClamped && d.Timestamp < request.From.ToUnixTimeMilliseconds(),
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
            Summary = (dayFilter is null ? "" : dayFilter.Summary(selection) + "\n")
                + Summarise(rows, startDate, endDate, useMmol, [.. omitted.Select(o => o.Field)], request.HistoryClamped ? request.From : null),
            Facts = FactsOf(rows, useMmol, omitted.Count == 0),
            Sentence = rows.Any(r => r["readings"] is int n && n > 0)
                ? "One row per local day; no figure is averaged across days."
                : "No glucose readings in this period.",
            Context = (dayFilter is null ? "" : dayFilter.Summary(selection) + " ") + string.Create(CultureInfo.InvariantCulture,
                $"Local days {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}. {rows.Count(r => r["readings"] is int n && n > 0)} of {rows.Count} days with readings."),
            FieldLabels = DailyProfileDataset.DayFieldLabels(Fields, dayFilter),
            Omitted = omitted,
            Window = days.Count == 0 ? null : new ReportWindow(days[0].Timestamp, days[^1].EndTimestamp),
        };
    }

    /// <summary>
    /// The days a day filter keeps, from the readings and records already read; exercise and illness are
    /// read as spans once for the range. A day that starts before what a history-clamped token reads is
    /// in neither group (<see cref="DayConditions"/>).
    /// </summary>
    private static async Task<DaySelection> SelectDaysAsync(
        ReportDayFilter filter, IReadOnlyList<Core.Models.PunchCardDay> days, IReadOnlyList<Core.Models.V4.SensorGlucose> glucose,
        InsulinRecords records, TimeZoneInfo zone, ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var starts = days.Select(d => d.Timestamp).Append(days[^1].EndTimestamp).ToList();
        var firstDay = DateOnly.ParseExact(days[0].Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = starts[^1];
        List<MillsInterval> exercise = [], illness = [];
        if (filter.Kind is ReportDayKind.Exercise or ReportDayKind.Illness)
        {
            var category = filter.Kind == ReportDayKind.Exercise ? StateSpanCategory.Exercise : StateSpanCategory.Illness;
            var spans = await request.Services.GetRequiredService<IStateSpanRepository>().GetByCategories(
                [category], DateTimeOffset.FromUnixTimeMilliseconds(starts[0]).UtcDateTime,
                DateTimeOffset.FromUnixTimeMilliseconds(end).UtcDateTime, cancellationToken);
            var intervals = spans.GetValueOrDefault(category, []).Select(s => new MillsInterval(s.StartMills, s.EndMills ?? end)).ToList();
            request.ReadLog.Record("state_spans", intervals.Count);
            (filter.Kind == ReportDayKind.Exercise ? exercise : illness).AddRange(intervals);
        }
        return DayConditions.Select(
            filter.ToCondition(), firstDay, starts, zone,
            [.. glucose.Select(g => new GlucoseSample(g.Mills, g.Mgdl)).OrderBy(g => g.Mills)],
            records.Carbs.Select(c => new CarbEvent(c.Mills, c.Carbs)),
            exercise, illness,
            request.HistoryClamped ? request.From.ToUnixTimeMilliseconds() : null);
    }

    /// <summary>The extremes across the days, each with its date as the note.</summary>
    internal static IReadOnlyList<ReportFact> FactsOf(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, bool useMmol, bool treatments)
    {
        var withData = rows.Where(r => r["readings"] is int n && n > 0).ToList();
        var facts = new List<ReportFact>();
        if (withData.Count > 0)
        {
            var best = withData.MaxBy(r => (double)r["timeInRange"]!)!;
            var worst = withData.MinBy(r => (double)r["timeInRange"]!)!;
            var mostBelow = withData.MaxBy(r => (double)r["timeBelow"]!)!;
            var highest = withData.MaxBy(r => (double)r["mean"]!)!;
            facts.Add(ReportFacts.Percent("Highest time in range", (double)best["timeInRange"]!, (string?)best["date"]).Of("timeInRange"));
            facts.Add(ReportFacts.Percent("Lowest time in range", (double)worst["timeInRange"]!, (string?)worst["date"]).Of("timeInRange"));
            facts.Add(ReportFacts.Percent("Most below range", (double)mostBelow["timeBelow"]!, (double)mostBelow["timeBelow"]! > 0 ? (string?)mostBelow["date"] : "on no day").Of("timeBelow"));
            facts.Add(new ReportFact("Highest mean", ReportFacts.Number((double)highest["mean"]!, useMmol ? "0.0" : "0"), ReportFacts.GlucoseUnit(useMmol), (string?)highest["date"], ["mean"]));
        }
        if (treatments && rows.Count > 0)
        {
            var most = rows.MaxBy(r => (double)r["totalInsulin"]!)!;
            var carbs = rows.MaxBy(r => (double)r["totalCarbs"]!)!;
            facts.Add(new ReportFact("Most insulin", ReportFacts.Number((double)most["totalInsulin"]!), "U", (string?)most["date"], ["totalInsulin"]));
            facts.Add(new ReportFact("Most carbs", ReportFacts.Number((double)carbs["totalCarbs"]!, "0"), "g", (string?)carbs["date"], ["totalCarbs"]));
        }
        return facts;
    }

    /// <summary>The days the rows cover, and the extremes across them; no figure is averaged across days.</summary>
    internal static string Summarise(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        DateOnly start,
        DateOnly end,
        bool useMmol,
        IReadOnlyList<string> omitted,
        DateTimeOffset? clampedFrom = null)
    {
        string N(object? value, string format) => value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : "no data";
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Daily summary over local days {start:yyyy-MM-dd} to {end:yyyy-MM-dd}."),
        };

        if (clampedFrom is { } cut && rows.FirstOrDefault(r => r.GetValueOrDefault("partial") is true) is { } first)
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"The day {first["date"]} is partial: this token reads only the last 24 hours, so its row starts at {cut:yyyy-MM-dd HH:mm} UTC."));

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
