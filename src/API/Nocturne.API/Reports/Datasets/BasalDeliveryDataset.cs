using System.Globalization;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// The basal the dashboard's basal track draws (<see cref="IChartDataService.GetBasalSeriesAsync"/>,
/// <c>GET /api/v4/chartdata/basal-series</c>): the delivered rate at each change and the rate the
/// schedule called for there. The scheduled rate is the profile's, so it needs therapy.read; the
/// service reads the profile for every caller, and the audit says so.
/// </summary>
internal sealed class BasalDeliveryDataset : IReportDataset
{
    public const int Budget = 2000;

    public string Name => BasalDeliveryParameters.Name;

    public string Label => "Basal delivery";

    public string Description => "The basal rate delivered over time, beside the rate of the schedule.";

    public Type ParametersType => typeof(BasalDeliveryParameters);

    public string RequiredScope => Scope.TreatmentsRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("time", "Time", ReportFieldKind.Time, null, "When the rate starts, Unix milliseconds."),
        new("delivered", "Delivered basal", ReportFieldKind.Number, "U/h", "The basal rate delivered.",
            Color: ChartColor.InsulinBasal, Step: true, Aggregate: ReportAggregate.Last),
        new("scheduled", "Scheduled basal", ReportFieldKind.Number, "U/h", "The rate the basal schedule called for, an override's scale included.",
            Scope.TherapyRead, ChartColor.InsulinTempBasal, Step: true, Aggregate: ReportAggregate.Last),
        new("origin", "Origin", ReportFieldKind.Category, null, "Where the delivered rate came from: Algorithm, Scheduled, Manual, Suspended or Inferred."),
    ];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) => mark switch
    {
        ReportMark.Area => new ReportLayerBindings { X = "time", Y = ["delivered"] },
        ReportMark.Line => new ReportLayerBindings { X = "time", Y = ["delivered", "scheduled"] },
        _ => null,
    };

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Area, ReportMark.Line];

    public TimeSpan DefaultRange => TimeSpan.FromHours(24);

    public TimeSpan MaxRange => TimeSpan.FromDays(14);

    public int PointBudget => Budget;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var from = request.From.ToUnixTimeMilliseconds();
        var to = request.To.ToUnixTimeMilliseconds();
        var series = (await request.Services.GetRequiredService<IChartDataService>().GetBasalSeriesAsync(from, to, cancellationToken))
            .OrderBy(p => p.Timestamp)
            .ToList();
        if (series.Count > 0)
            request.ReadLog.Record("treatments", series.Count);

        // The series is built on the profile's schedule whatever the token holds (a scheduled or
        // inferred row's delivered rate is the schedule's), so the profile is read in every case.
        if (series.Count > 0)
            request.ReadLog.Record("profiles", 0);
        var therapy = request.HasScope(Scope.TherapyRead);

        var interval = GlucoseSeriesDataset.IntervalFor(request.From, request.To, 1, Budget);
        var kept = Thin(series, from, interval);
        var rows = kept.Select(p => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["time"] = p.Timestamp,
            ["delivered"] = Math.Round(p.Rate, 3),
            ["scheduled"] = therapy && p.ScheduledRate is { } s ? Math.Round(s, 3) : null,
            ["origin"] = p.Origin.ToString(),
        }).ToList();

        IReadOnlyList<ReportOmittedField> omitted = therapy ? [] : [new ReportOmittedField("scheduled", Scope.TherapyRead)];
        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = Summarise(series, request.From, request.To, therapy, [.. omitted.Select(o => o.Field)]),
            Facts = FactsOf(series, therapy),
            Sentence = series.Count == 0 ? "No basal records in this period."
                : "Changes by origin: " + string.Join(", ", series.GroupBy(p => p.Origin).OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key} {g.Count()}")) + ".",
            Context = $"{ReportFacts.Window(request.From, request.To, request.Zone)}. {ReportFacts.Plural(series.Count, "rate change")}.",
            Omitted = omitted,
            Resolution = new ReportResolution(1, interval, series.Count, rows.Count, rows.Count < series.Count),
        };
    }

    /// <summary>The first change in each slot of <paramref name="intervalMinutes"/>, every change when they fit the budget.</summary>
    internal static List<BasalPoint> Thin(IReadOnlyList<BasalPoint> series, long fromMills, int intervalMinutes)
    {
        if (series.Count <= Budget)
            return [.. series];
        var slot = intervalMinutes * 60_000L;
        return [.. series.GroupBy(p => Math.Max(0, p.Timestamp - fromMills) / slot).Select(g => g.First())];
    }

    /// <summary>The delivered and scheduled rates' extremes, and the changes above and below the schedule.</summary>
    internal static IReadOnlyList<ReportFact> FactsOf(IReadOnlyList<BasalPoint> series, bool therapy)
    {
        if (series.Count == 0)
            return [];
        static string N(double value) => ReportFacts.Number(value, "0.##");
        var facts = new List<ReportFact>
        {
            new("Lowest delivered", N(series.Min(p => p.Rate)), "U/h"),
            new("Highest delivered", N(series.Max(p => p.Rate)), "U/h"),
        };
        var scheduled = series.Where(p => p.ScheduledRate is not null).Select(p => p.ScheduledRate!.Value).ToList();
        if (therapy && scheduled.Count > 0)
        {
            facts.Add(new ReportFact("Scheduled", scheduled.Min() == scheduled.Max() ? N(scheduled.Min()) : $"{N(scheduled.Min())}-{N(scheduled.Max())}", "U/h"));
            facts.Add(ReportFacts.Count("Above schedule", series.Count(p => p.ScheduledRate is { } s && p.Rate > s), "changes"));
            facts.Add(ReportFacts.Count("Below schedule", series.Count(p => p.ScheduledRate is { } s && p.Rate < s), "changes"));
        }
        return facts;
    }

    /// <summary>The rates' extremes and the changes by origin: counts of rows, not a time-weighted total.</summary>
    internal static string Summarise(
        IReadOnlyList<BasalPoint> series, DateTimeOffset from, DateTimeOffset to, bool therapy, IReadOnlyList<string> omitted)
    {
        static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Basal delivery from {from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm} UTC."),
        };
        if (series.Count == 0)
        {
            lines.Add("No basal records in this period.");
        }
        else
        {
            lines.Add($"Rate changes: {series.Count}. Delivered rate from {N(series.Min(p => p.Rate))} to {N(series.Max(p => p.Rate))} U/h.");
            var scheduled = series.Where(p => p.ScheduledRate is not null).Select(p => p.ScheduledRate!.Value).ToList();
            if (therapy && scheduled.Count > 0)
            {
                lines.Add($"Scheduled rate from {N(scheduled.Min())} to {N(scheduled.Max())} U/h.");
                var above = series.Count(p => p.ScheduledRate is { } s && p.Rate > s);
                var below = series.Count(p => p.ScheduledRate is { } s && p.Rate < s);
                lines.Add($"Changes to a rate above the schedule: {above}. Below it: {below}.");
            }
            lines.Add("Changes by origin: " + string.Join(", ", series.GroupBy(p => p.Origin).OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key} {g.Count()}")) + ".");
        }
        if (omitted.Count > 0)
            lines.Add("The schedule on its own is omitted: it needs the therapy.read scope, which this token does not hold. "
                + "A row from the schedule still shows the scheduled rate as the delivered rate.");
        return string.Join('\n', lines);
    }
}
