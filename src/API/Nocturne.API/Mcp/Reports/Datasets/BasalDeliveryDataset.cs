using System.Globalization;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Mcp.Reports.Datasets;

/// <summary>
/// The basal the dashboard's basal track draws (<see cref="IChartDataService.GetBasalSeriesAsync"/>,
/// <c>GET /api/v4/chartdata/basal-series</c>): the delivered rate at each change and the rate the
/// schedule called for there. The scheduled rate is the profile's, so it needs therapy.read.
/// </summary>
internal sealed class BasalDeliveryDataset : IReportDataset
{
    public const int Budget = 2000;

    public string Name => BasalDeliveryParameters.Name;

    public string Description =>
        "Basal delivery: the basal rate delivered at each change in the range, beside the rate the schedule called for "
        + "(with therapy.read) and where the rate came from (Algorithm, Scheduled, ...), as the dashboard's basal track draws "
        + "them. Each rate holds until the next row. Draw as time_series.";

    public Type ParametersType => typeof(BasalDeliveryParameters);

    public string RequiredScope => Scope.TreatmentsRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("time", "Time", ReportFieldKind.Time, null, "When the rate starts, Unix milliseconds."),
        new("delivered", "Delivered basal", ReportFieldKind.Number, "U/h", "The basal rate delivered.",
            Color: ChartColor.InsulinBasal, Step: true),
        new("scheduled", "Scheduled basal", ReportFieldKind.Number, "U/h", "The rate the basal schedule called for, an override's scale included.",
            Scope.TherapyRead, ChartColor.InsulinTempBasal, Step: true),
        new("origin", "Origin", ReportFieldKind.Category, null, "Where the delivered rate came from: Algorithm, Scheduled, Manual, Suspended or Inferred."),
    ];

    public ReportBindings? DefaultBindings(ReportPrimitive primitive) =>
        new() { X = "time", Y = ["delivered", "scheduled"] };

    public IReadOnlyList<ReportPrimitive> AllowedPrimitives { get; } = [ReportPrimitive.TimeSeries];

    public TimeSpan DefaultRange => TimeSpan.FromHours(24);

    public TimeSpan MaxRange(ReportPrimitive primitive) => TimeSpan.FromDays(14);

    public int PointBudget => Budget;

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> SampleRows { get; } =
    [
        new Dictionary<string, object?> { ["time"] = 1793260800000L, ["delivered"] = 0.85, ["scheduled"] = 0.85, ["origin"] = "Scheduled" },
        new Dictionary<string, object?> { ["time"] = 1793261100000L, ["delivered"] = 1.4, ["scheduled"] = 0.85, ["origin"] = "Algorithm" },
    ];

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

        var therapy = request.HasScope(Scope.TherapyRead);
        if (therapy && series.Any(p => p.ScheduledRate is not null))
            request.ReadLog.Record("profiles", 0);

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
            lines.Add($"Fields omitted: {string.Join(", ", omitted)} (scope not granted).");
        return string.Join('\n', lines);
    }
}
