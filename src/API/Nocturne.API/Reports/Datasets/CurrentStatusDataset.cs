using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Widget;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// One row as it is now: the hours since the latest canonical reading (<see cref="ICanonicalGlucoseService.GetLatestAsync(CancellationToken)"/>,
/// as the People overview reads it) and, with alerts.read, the hours left on the sensor and the site trackers, from
/// each running tracker's expected end as the overview condenses it (<see cref="TrackerStatusCondenser.TrackerStatusNow.HoursLeft"/>).
/// The range is not read.
/// </summary>
internal sealed class CurrentStatusDataset : IReportDataset
{
    internal const string HoursSinceReading = "hoursSinceReading";
    internal const string SensorHoursLeft = "sensorHoursLeft";
    internal const string SiteHoursLeft = "siteHoursLeft";

    /// <summary>The fields read from the trackers, left out without <see cref="Scope.AlertsRead"/>.</summary>
    internal static readonly IReadOnlyList<string> TrackerFields = [SensorHoursLeft, SiteHoursLeft];

    public string Name => CurrentStatusParameters.Name;

    public string Label => "Current status";

    public string Description => "The hours since the last glucose reading, and the hours left on the sensor and the site, now.";

    public Type ParametersType => typeof(CurrentStatusParameters);

    public string RequiredScope => Scope.GlucoseRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new(HoursSinceReading, "Hours since the last reading", ReportFieldKind.Number, "h",
            "Hours from the latest glucose reading to now.") { FlagFact = ReportFlagFact.Live },
        new(SensorHoursLeft, "Sensor hours left", ReportFieldKind.Number, "h",
            "Hours until the sensor tracker is due, now; negative once it is past due. The soonest when more than one runs.",
            Scope.AlertsRead) { FlagFact = ReportFlagFact.Live },
        new(SiteHoursLeft, "Site hours left", ReportFieldKind.Number, "h",
            "Hours until the site (cannula or pod) tracker is due, now; negative once it is past due. The soonest when more than one runs.",
            Scope.AlertsRead) { FlagFact = ReportFlagFact.Live },
    ];

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.StatTiles, ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [];

    public TimeSpan DefaultRange => TimeSpan.FromHours(24);

    public TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => 1;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var services = request.Services;
        var now = request.Now ?? services.GetRequiredService<TimeProvider>().GetUtcNow();
        var latest = await services.GetRequiredService<ICanonicalGlucoseService>().GetLatestAsync(cancellationToken);
        if (latest is not null)
            request.ReadLog.Record("glucose", 1);

        var row = new Dictionary<string, object?>
        {
            [HoursSinceReading] = latest is null
                ? null
                : ScaledRounding.Round(Math.Max(0, (now.UtcDateTime - latest.Timestamp).TotalHours), 1),
        };

        var trackers = request.HasScope(Scope.AlertsRead);
        if (trackers)
        {
            var statuses = TrackerStatusCondenser.All(
                await services.GetRequiredService<ITrackerRepository>().GetActiveInstancesAsync(cancellationToken), now.UtcDateTime);
            if (statuses.Count > 0)
                request.ReadLog.Record("trackers", statuses.Count);
            row[SensorHoursLeft] = Soonest(statuses, TrackerCategory.Sensor);
            row[SiteHoursLeft] = Soonest(statuses, TrackerCategory.Cannula, TrackerCategory.Pod);
        }

        IReadOnlyList<ReportOmittedField> omitted = trackers
            ? []
            : [.. TrackerFields.Select(f => new ReportOmittedField(f, Scope.AlertsRead))];
        var lines = Lines(row, latest, request.Clock).ToList();
        if (omitted.Count > 0)
            lines.Add($"Fields omitted: {string.Join(", ", omitted.Select(o => o.Field))} (scope not granted).");

        return new ReportDatasetOutput
        {
            Rows = [row],
            Summary = string.Join('\n', lines),
            Facts = FactsOf(row),
            Sentence = latest is null ? "No glucose readings." : null,
            Context = $"Now, {ReportFacts.Instant(now, request.Clock)}.",
            Omitted = omitted,
        };
    }

    private static double? Soonest(IEnumerable<TrackerStatusCondenser.TrackerStatusNow> statuses, params TrackerCategory[] categories) =>
        statuses.Where(s => categories.Contains(s.Status.Category)).Select(s => s.HoursLeft).Min();

    private static IEnumerable<string> Lines(IReadOnlyDictionary<string, object?> row, SensorGlucose? latest, ReportClock clock)
    {
        yield return latest is null
            ? "Last glucose reading: none."
            : $"Last glucose reading: {ReportFacts.Instant(new DateTimeOffset(latest.Timestamp, TimeSpan.Zero), clock)}, "
                + $"{ReportFacts.Number((double)row[HoursSinceReading]!)} hours ago.";
        if (row.ContainsKey(SensorHoursLeft))
            yield return row[SensorHoursLeft] is double sensor ? $"Sensor hours left: {ReportFacts.Number(sensor)}." : "Sensor hours left: no running sensor tracker.";
        if (row.ContainsKey(SiteHoursLeft))
            yield return row[SiteHoursLeft] is double site ? $"Site hours left: {ReportFacts.Number(site)}." : "Site hours left: no running site tracker.";
    }

    private static IReadOnlyList<ReportFact> FactsOf(IReadOnlyDictionary<string, object?> row)
    {
        var facts = new List<ReportFact>();
        if (row.GetValueOrDefault(HoursSinceReading) is double since)
            facts.Add(new ReportFact("Last reading", ReportFacts.Number(since), "h", "ago", [HoursSinceReading]));
        if (row.GetValueOrDefault(SensorHoursLeft) is double sensor)
            facts.Add(new ReportFact("Sensor", ReportFacts.Number(sensor), "h", "left", [SensorHoursLeft]));
        if (row.GetValueOrDefault(SiteHoursLeft) is double site)
            facts.Add(new ReportFact("Site", ReportFacts.Number(site), "h", "left", [SiteHoursLeft]));
        return facts;
    }
}
