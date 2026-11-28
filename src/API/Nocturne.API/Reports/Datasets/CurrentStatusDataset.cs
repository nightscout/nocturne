using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Widget;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// One row as it is now: the hours since the latest canonical reading (<see cref="ICanonicalGlucoseService.GetLatestAsync(CancellationToken)"/>,
/// as the People overview reads it) and, with alerts.read, the hours left on the sensor and the site trackers, from
/// each running tracker's expected end as the overview condenses it (<see cref="TrackerStatusCondenser.TrackerStatusNow.HoursLeft"/>).
/// With treatments.read it also gives the insulin and carbs on board, as the overview reads them
/// (<see cref="OnBoardFigures"/>); one that is not known is no data. The range is not read.
/// </summary>
internal sealed class CurrentStatusDataset : IReportDataset
{
    internal const string HoursSinceReading = "hoursSinceReading";
    internal const string SensorHoursLeft = "sensorHoursLeft";
    internal const string SiteHoursLeft = "siteHoursLeft";
    internal const string InsulinOnBoard = "insulinOnBoard";
    internal const string CarbsOnBoard = "carbsOnBoard";

    private const string InsulinFormat = "0.##";
    private const string CarbsFormat = "0";

    /// <summary>The fields read from the trackers, left out without <see cref="Scope.AlertsRead"/>.</summary>
    internal static readonly IReadOnlyList<string> TrackerFields = [SensorHoursLeft, SiteHoursLeft];

    /// <summary>The fields read from the treatments, left out without <see cref="Scope.TreatmentsRead"/>.</summary>
    internal static readonly IReadOnlyList<string> OnBoardFields = [InsulinOnBoard, CarbsOnBoard];

    public string Name => CurrentStatusParameters.Name;

    public string Label => "Current status";

    public string Description =>
        "The hours since the last glucose reading, the hours left on the sensor and the site, and the insulin and carbs on board, now.";

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
        new(InsulinOnBoard, "Insulin on board", ReportFieldKind.Number, "U",
            "Units of insulin still working, now. Blank when it is not known.",
            Scope.TreatmentsRead) { FlagFact = ReportFlagFact.Live },
        new(CarbsOnBoard, "Carbs on board", ReportFieldKind.Number, "g",
            "Grams of carbs still to be absorbed, now. Blank when it is not known.",
            Scope.TreatmentsRead) { FlagFact = ReportFlagFact.Live },
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
        var trackers = request.HasScope(Scope.AlertsRead);
        var active = trackers
            ? await services.GetRequiredService<ITrackerRepository>().GetActiveInstancesAsync(cancellationToken)
            : null;
        var therapy = request.HasScope(Scope.TreatmentsRead);
        var onBoard = therapy
            ? await OnBoardFigures.ReadAsync(services.GetRequiredService<IIobCobReader>(), now, cancellationToken)
            : null;
        var row = RowOf(latest, active, onBoard, now, request.ReadLog);

        IReadOnlyList<ReportOmittedField> omitted =
        [
            .. OmittedWithout(trackers, TrackerFields, Scope.AlertsRead),
            .. OmittedWithout(therapy, OnBoardFields, Scope.TreatmentsRead),
        ];
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

    private static IEnumerable<ReportOmittedField> OmittedWithout(bool granted, IEnumerable<string> fields, string scope) =>
        granted ? [] : fields.Select(f => new ReportOmittedField(f, scope));

    /// <summary>
    /// The row from a latest reading, the running trackers and the on board figures already read, and what they took
    /// recorded in <paramref name="readLog"/>. <paramref name="active"/> is null for a reader without
    /// <see cref="Scope.AlertsRead"/>, whose row leaves the tracker fields out, and <paramref name="onBoard"/> for one
    /// without <see cref="Scope.TreatmentsRead"/>, whose row leaves the on board fields out. The People overview reads
    /// all of them for each person, so it judges these facts on its own reads.
    /// </summary>
    internal static Dictionary<string, object?> RowOf(
        SensorGlucose? latest,
        IEnumerable<TrackerInstance>? active,
        OnBoardFigures? onBoard,
        DateTimeOffset now,
        ReportReadLog readLog)
    {
        if (latest is not null)
            readLog.Record("glucose", 1);

        var row = new Dictionary<string, object?>
        {
            [HoursSinceReading] = latest is null
                ? null
                : ScaledRounding.Round(Math.Max(0, (now.UtcDateTime - latest.Timestamp).TotalHours), 1),
        };
        if (active is not null)
        {
            var statuses = TrackerStatusCondenser.All(active, now.UtcDateTime);
            if (statuses.Count > 0)
                readLog.Record("trackers", statuses.Count);
            row[SensorHoursLeft] = Soonest(statuses, TrackerCategory.Sensor);
            row[SiteHoursLeft] = Soonest(statuses, TrackerCategory.Cannula, TrackerCategory.Pod);
        }

        if (onBoard is not null)
        {
            readLog.Record("treatments", 0);
            row[InsulinOnBoard] = onBoard.Insulin;
            row[CarbsOnBoard] = onBoard.Carbs;
        }
        return row;
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
        if (row.ContainsKey(InsulinOnBoard))
            yield return row[InsulinOnBoard] is double insulin ? $"Insulin on board: {ReportFacts.Number(insulin, InsulinFormat)} U." : "Insulin on board: not known.";
        if (row.ContainsKey(CarbsOnBoard))
            yield return row[CarbsOnBoard] is double carbs ? $"Carbs on board: {ReportFacts.Number(carbs, CarbsFormat)} g." : "Carbs on board: not known.";
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
        if (row.GetValueOrDefault(InsulinOnBoard) is double insulin)
            facts.Add(new ReportFact("Insulin on board", ReportFacts.Number(insulin, InsulinFormat), "U", null, [InsulinOnBoard]));
        if (row.GetValueOrDefault(CarbsOnBoard) is double carbs)
            facts.Add(new ReportFact("Carbs on board", ReportFacts.Number(carbs, CarbsFormat), "g", null, [CarbsOnBoard]));
        return facts;
    }
}
