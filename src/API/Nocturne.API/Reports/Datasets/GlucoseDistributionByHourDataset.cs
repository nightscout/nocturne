using System.Globalization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using static Nocturne.API.Reports.Datasets.LoopAnalysisDatasetRun;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// The hourly distribution the Glucose distribution report draws
/// (<c>ReportAnalysisResult.AveragedStats</c>, from <see cref="IStatisticsService.CalculateAveragedStats"/>
/// over the canonical stream in the tenant zone): one row per local hour with its percentiles and
/// the share of time in each glucose range.
/// </summary>
internal sealed class GlucoseDistributionByHourDataset : IReportDataset
{
    public string Name => GlucoseDistributionByHourParameters.Name;

    public string Label => "Glucose by hour of day";

    public string Description => "Glucose percentiles and the time in each range for each hour of the day.";

    public Type ParametersType => typeof(GlucoseDistributionByHourParameters);

    public string RequiredScope => Scope.GlucoseRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("hour", "Hour", ReportFieldKind.TimeOfDay, "min", "Start of the local hour, minutes after midnight."),
        new("glucoseP10", "Glucose P10", ReportFieldKind.Number, ReportField.GlucoseUnit, "10th percentile of the hour's readings.", Aggregate: ReportAggregate.Mean),
        new("glucoseP25", "Glucose P25", ReportFieldKind.Number, ReportField.GlucoseUnit, "25th percentile of the hour's readings.", Aggregate: ReportAggregate.Mean),
        new("glucoseP50", "Glucose median", ReportFieldKind.Number, ReportField.GlucoseUnit, "Median of the hour's readings.",
            Color: ChartColor.GlucoseInRange, GlucoseScale: true, Aggregate: ReportAggregate.Mean),
        new("glucoseP75", "Glucose P75", ReportFieldKind.Number, ReportField.GlucoseUnit, "75th percentile of the hour's readings.", Aggregate: ReportAggregate.Mean),
        new("glucoseP90", "Glucose P90", ReportFieldKind.Number, ReportField.GlucoseUnit, "90th percentile of the hour's readings.", Aggregate: ReportAggregate.Mean),
        new("mean", "Mean", ReportFieldKind.Number, ReportField.GlucoseUnit, "Mean of the hour's readings.", Aggregate: ReportAggregate.Mean),
        new("readings", "Readings", ReportFieldKind.Number, null, "Readings in the hour across the range.", Aggregate: ReportAggregate.Sum),
        new("days", "Days", ReportFieldKind.Number, null, "Local days with a reading in the hour.", Aggregate: ReportAggregate.Max),
        new("veryLow", "Very low", ReportFieldKind.Percent, "%", "Percent of the hour's covered time very low.", Color: ChartColor.GlucoseVeryLow, Aggregate: ReportAggregate.Mean),
        new("low", "Low", ReportFieldKind.Percent, "%", "Percent of the hour's covered time low.", Color: ChartColor.GlucoseLow, Aggregate: ReportAggregate.Mean),
        new("tightRange", "Tight range", ReportFieldKind.Percent, "%", "Percent of the hour's covered time in the tight range.", Color: ChartColor.GlucoseInRange, Aggregate: ReportAggregate.Mean),
        new("aboveTightRange", "Above tight range", ReportFieldKind.Percent, "%", "Percent of the hour's covered time above the tight range but in range.", Aggregate: ReportAggregate.Mean),
        new("high", "High", ReportFieldKind.Percent, "%", "Percent of the hour's covered time high.", Color: ChartColor.GlucoseHigh, Aggregate: ReportAggregate.Mean),
        new("veryHigh", "Very high", ReportFieldKind.Percent, "%", "Percent of the hour's covered time very high.", Color: ChartColor.GlucoseVeryHigh, Aggregate: ReportAggregate.Mean),
        new(ReportField.SentenceField, "Sentence", ReportFieldKind.Category, null,
            "One sentence that states the hour's glucose figures; the chart shows it for the hour under the pointer.", Companion: true),
    ];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) => mark switch
    {
        ReportMark.Band => new ReportLayerBindings
        {
            X = "hour",
            Y = ["glucoseP50"],
            Edges = ["glucoseP10", "glucoseP25", "glucoseP75", "glucoseP90"],
        },
        ReportMark.Line => new ReportLayerBindings { X = "hour", Y = ["glucoseP50"] },
        ReportMark.Bars => new ReportLayerBindings { X = "hour", Y = ["veryLow", "low", "tightRange", "aboveTightRange", "high", "veryHigh"], Stacked = true },
        _ => null,
    };

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Band, ReportMark.Line, ReportMark.Bars];

    public TimeSpan DefaultRange => TimeSpan.FromDays(14);

    public TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => 24;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters)
    {
        if (((GlucoseDistributionByHourParameters)parameters).DayOfWeek is { } days && days.Distinct().Count() != days.Count)
            yield return ("dayOfWeek", "A weekday is named twice.");
    }

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var services = request.Services;
        var parameters = (GlucoseDistributionByHourParameters)request.Parameters;
        var entries = await request.Cache.GlucoseAsync(request, cancellationToken);
        var clock = await services.GetRequiredService<TenantClockResolver>().ResolveAsync(cancellationToken);
        if (parameters.DayOfWeek is { Count: > 0 } weekdays)
        {
            var days = weekdays.Select(d => d.ToDayOfWeek()).ToHashSet();
            var zone = clock.Zone ?? TimeZoneInfo.Utc;
            entries = [.. entries.Where(e => days.Contains(TimeZoneInfo.ConvertTimeFromUtc(e.Timestamp, zone).DayOfWeek))];
        }
        var hours = services.GetRequiredService<IStatisticsService>().CalculateAveragedStats(entries, clock.Zone).ToList();

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
                [ReportField.SentenceField] = HourSentence(h, useMmol),
            };
        }).ToList();

        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = Summarise(hours, request.From, request.To, clock.Zone is null, useMmol),
            Facts = FactsOf(hours, useMmol),
            Sentence = SentenceOf(hours, clock.Zone is null),
            Context = $"{ReportFacts.Window(request.From, request.To, request.Zone)}. {ReportFacts.Plural(hours.Sum(h => h.Count), "reading")}.",
        };
    }

    /// <summary>The hours with the highest and lowest median, and the most time above and below range.</summary>
    internal static IReadOnlyList<ReportFact> FactsOf(IReadOnlyList<AveragedStats> hours, bool useMmol)
    {
        var withData = hours.Where(h => h.Count > 0).ToList();
        if (withData.Count == 0)
            return [];
        string From(int hour) => $"from {Clock(hour * 60)}";
        var highest = withData.MaxBy(h => h.Percentiles.P50)!;
        var lowest = withData.MinBy(h => h.Percentiles.P50)!;
        var above = withData.MaxBy(h => h.TimeInRange.High + h.TimeInRange.VeryHigh)!;
        var below = withData.MaxBy(h => h.TimeInRange.VeryLow + h.TimeInRange.Low)!;
        return
        [
            ReportFacts.Glucose("Highest median", highest.Percentiles.P50, useMmol, From(highest.Hour)),
            ReportFacts.Glucose("Lowest median", lowest.Percentiles.P50, useMmol, From(lowest.Hour)),
            ReportFacts.Percent("Most above range", above.TimeInRange.High + above.TimeInRange.VeryHigh, From(above.Hour)),
            ReportFacts.Percent("Most below range", below.TimeInRange.VeryLow + below.TimeInRange.Low,
                below.TimeInRange.VeryLow + below.TimeInRange.Low > 0 ? From(below.Hour) : "in no hour"),
        ];
    }

    internal static string SentenceOf(IReadOnlyList<AveragedStats> hours, bool utc)
    {
        var withData = hours.Count(h => h.Count > 0);
        if (withData == 0)
            return "No glucose readings in this period.";
        return $"Readings in {withData} of 24 hours, " + (utc ? "in UTC hours: the tenant has no time zone set." : "in the tenant's local hours.");
    }

    /// <summary>"From 07:00 to 08:00: median 128 mg/dL, middle half 108 to 155 mg/dL, 168 readings on 14 days."</summary>
    internal static string HourSentence(AveragedStats hour, bool useMmol)
    {
        var head = $"From {Clock(hour.Hour * 60)} to {Clock((hour.Hour + 1) % 24 * 60)}";
        if (hour.Count == 0)
            return $"{head}: no readings.";
        var unit = useMmol ? "mmol/L" : "mg/dL";
        string G(double mgdl) => Number(Glucose(mgdl, useMmol)!.Value, useMmol ? "0.0" : "0");
        return $"{head}: median {G(hour.Percentiles.P50)} {unit}, middle half {G(hour.Percentiles.P25)} to {G(hour.Percentiles.P75)} {unit}, "
            + $"{hour.Count} {(hour.Count == 1 ? "reading" : "readings")} on {hour.DayCount} {(hour.DayCount == 1 ? "day" : "days")}.";
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
