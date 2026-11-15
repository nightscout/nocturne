using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Helpers;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// Each device event the dashboard chart marks (<see cref="ChartDataService.BuildDeviceEventMarkers"/>), as a tick in
/// one of three lanes, read through the request's own repository: <c>device_events</c> is shared under
/// <c>devices.read</c> and clamped like every time series. A profile switch is not here: the profiles lane draws it.
/// </summary>
internal sealed class DeviceEventsDataset : LaneDataset
{
    public override string Name => DeviceEventsParameters.Name;

    public override string Label => "Device events";

    public override string Description => "Sensor changes, site and insulin changes, and pump events, each at the time it happened.";

    public override Type ParametersType => typeof(DeviceEventsParameters);

    public override string RequiredScope => Scope.DevicesRead;

    protected override string LaneWords => "Sensor, Site and insulin, or Pump: the lane the event is drawn in.";

    protected override string LabelWords => "The event: Site change, Sensor start, Pump suspend. An event Nocturne found from a gap in the data says so.";

    protected override string Noun => "event";

    protected override string ReadName => "device_events";

    protected override bool Instants => true;

    public override TimeSpan DefaultRange => TimeSpan.FromDays(30);

    public override TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public override IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters)
    {
        if (((DeviceEventsParameters)parameters).Groups is { } groups && groups.Distinct().Count() != groups.Count)
            yield return ("groups", "A lane is named twice.");
    }

    protected override async Task<IReadOnlyList<Span>> ReadAsync(ReportDatasetRequest request, long from, long to, int limit, CancellationToken cancellationToken)
    {
        var asked = ((DeviceEventsParameters)request.Parameters).Groups is { Count: > 0 } groups
            ? groups.ToHashSet()
            : Enum.GetValues<ReportDeviceEventGroup>().ToHashSet();
        var events = await request.Services.GetRequiredService<IDeviceEventRepository>().GetAsync(
            Utc(from), Utc(to), null, null, V4ReadLimits.MaxAnalyticsSpanDays * 50, 0, descending: false, ct: cancellationToken);
        return [.. events
            .Where(e => GroupOf(e.EventType) is { } g && asked.Contains(g))
            .Take(limit)
            .Select(e => new Span(GroupWords(GroupOf(e.EventType)!.Value), e.IsInferred ? $"{Words(e.EventType)} (found from a gap)" : Words(e.EventType),
                e.Mills, e.Mills, ChartColorMapper.FromDeviceEvent(e.EventType)))];
    }

    /// <summary>The lane an event is drawn in, or null for one no lane draws.</summary>
    internal static ReportDeviceEventGroup? GroupOf(DeviceEventType type) => type switch
    {
        DeviceEventType.SensorStart or DeviceEventType.SensorChange or DeviceEventType.SensorStop
            or DeviceEventType.TransmitterSensorInsert => ReportDeviceEventGroup.Sensor,
        DeviceEventType.SiteChange or DeviceEventType.CannulaChange or DeviceEventType.PodChange or DeviceEventType.PodActivated
            or DeviceEventType.PodDeactivated or DeviceEventType.InsulinChange or DeviceEventType.ReservoirChange or DeviceEventType.Rewind
            or DeviceEventType.Priming or DeviceEventType.TubePriming or DeviceEventType.NeedlePriming => ReportDeviceEventGroup.Site,
        DeviceEventType.PumpBatteryChange or DeviceEventType.PumpSuspend or DeviceEventType.PumpResume or DeviceEventType.DateChanged
            or DeviceEventType.TimeChanged or DeviceEventType.BolusMaxChanged or DeviceEventType.BasalMaxChanged => ReportDeviceEventGroup.Pump,
        _ => null,
    };

    internal static string GroupWords(ReportDeviceEventGroup group) => group switch
    {
        ReportDeviceEventGroup.Sensor => "Sensor",
        ReportDeviceEventGroup.Site => "Site and insulin",
        ReportDeviceEventGroup.Pump => "Pump",
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, null),
    };

    internal static string Words(DeviceEventType type) => type switch
    {
        DeviceEventType.SensorStart => "Sensor start",
        DeviceEventType.SensorChange => "Sensor change",
        DeviceEventType.SensorStop => "Sensor stop",
        DeviceEventType.TransmitterSensorInsert => "Transmitter and sensor insert",
        DeviceEventType.SiteChange => "Site change",
        DeviceEventType.CannulaChange => "Cannula change",
        DeviceEventType.PodChange => "Pod change",
        DeviceEventType.PodActivated => "Pod start",
        DeviceEventType.PodDeactivated => "Pod stop",
        DeviceEventType.InsulinChange => "Insulin change",
        DeviceEventType.ReservoirChange => "Reservoir change",
        DeviceEventType.Rewind => "Rewind",
        DeviceEventType.Priming => "Priming",
        DeviceEventType.TubePriming => "Tube priming",
        DeviceEventType.NeedlePriming => "Needle priming",
        DeviceEventType.PumpBatteryChange => "Pump battery change",
        DeviceEventType.PumpSuspend => "Pump suspend",
        DeviceEventType.PumpResume => "Pump resume",
        DeviceEventType.DateChanged => "Pump date change",
        DeviceEventType.TimeChanged => "Pump time change",
        DeviceEventType.BolusMaxChanged => "Maximum bolus change",
        DeviceEventType.BasalMaxChanged => "Maximum basal change",
        _ => type.ToString(),
    };
}

/// <summary>
/// Each alarm, hazard, warning and notice a device reported, as the dashboard chart marks them
/// (<see cref="ChartDataService.MapSystemEvents"/>), in a lane per device. Read through the request's own
/// repository, so RLS decides what a reader sees: <c>system_events</c> is hidden from shares and clamped for a
/// member (<see cref="ShareDataCategories.HiddenRecencyColumns"/>); the chart serves it under <c>devices.read</c>.
/// </summary>
internal sealed class SystemEventsDataset : LaneDataset
{
    public override string Name => SystemEventsParameters.Name;

    public override string Label => "Device alarms";

    public override string Description => "Each alarm, hazard, warning and notice a pump, CGM or uploader reported.";

    public override Type ParametersType => typeof(SystemEventsParameters);

    public override string RequiredScope => Scope.DevicesRead;

    protected override string LaneWords => "Pump, CGM or Connection: the lane the alarm is drawn in.";

    protected override string LabelWords => "Alarm, Hazard, Warning or Notice, and what the device said.";

    protected override string Noun => "alarm";

    protected override string ReadName => "system_events";

    protected override bool Instants => true;

    public override TimeSpan DefaultRange => TimeSpan.FromDays(14);

    public override TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    protected override async Task<IReadOnlyList<Span>> ReadAsync(ReportDatasetRequest request, long from, long to, int limit, CancellationToken cancellationToken)
    {
        var events = await request.Services.GetRequiredService<ISystemEventRepository>().GetSystemEventsAsync(
            from: from, to: to, count: limit, cancellationToken: cancellationToken);
        return [.. events.Select(e => new Span(CategoryWords(e.Category), LabelOf(e), e.Mills, e.Mills, ChartColorMapper.FromSystemEvent(e.EventType)))];
    }

    internal static string LabelOf(SystemEvent e)
    {
        var what = !string.IsNullOrWhiteSpace(e.Description) ? e.Description.Trim() : e.Code;
        var type = e.EventType switch
        {
            SystemEventType.Alarm => "Alarm",
            SystemEventType.Hazard => "Hazard",
            SystemEventType.Warning => "Warning",
            _ => "Notice",
        };
        return string.IsNullOrWhiteSpace(what) ? type
            : what.Length > TreatmentsDataset.MaxDetailLength ? $"{type}: {what[..TreatmentsDataset.MaxDetailLength]}..." : $"{type}: {what}";
    }

    internal static string CategoryWords(SystemEventCategory category) => category switch
    {
        SystemEventCategory.Pump => "Pump",
        SystemEventCategory.Cgm => "CGM",
        SystemEventCategory.Connectivity => "Connection",
        _ => category.ToString(),
    };
}

/// <summary>
/// Each tracker instance (a sensor, site, reservoir or other consumable in use) from its start to its completion, in
/// a lane per category, named with how long it lasted. Trackers have no share category, and
/// <see cref="Authorization.TrackerReadAccess"/> admits an account holding <c>alerts.read</c>; every report run is
/// an account's (<c>CustomReportsController</c> refuses share and guest links), so the scope is what is left to
/// check. Read in the request's own context, so RLS decides what a reader sees: a clamped member sees an instance
/// still running, or completed in its window.
/// </summary>
internal sealed class TrackersDataset : LaneDataset
{
    public override string Name => TrackersParameters.Name;

    public override string Label => "Trackers";

    public override string Description => "Each tracked sensor, site, reservoir or other consumable, from its start to its end, and how long it lasted.";

    public override Type ParametersType => typeof(TrackersParameters);

    public override string RequiredScope => Scope.AlertsRead;

    protected override string LaneWords => "The tracker's category: Sensor, Cannula, Pod, Reservoir, Battery and others.";

    protected override string LabelWords => "The tracker's name and how many days it lasted, or has lasted so far.";

    protected override string Noun => "tracker";

    protected override string ReadName => "trackers";

    public override TimeSpan DefaultRange => TimeSpan.FromDays(30);

    public override TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    protected override async Task<IReadOnlyList<Span>> ReadAsync(ReportDatasetRequest request, long from, long to, int limit, CancellationToken cancellationToken)
    {
        var (start, end) = (Utc(from), Utc(to));
        await using var db = await request.Services.GetRequiredService<INocturneDbContextFactory>().CreateAsync(cancellationToken);
        var instances = await db.TrackerInstances.AsNoTracking()
            .Where(i => i.StartedAt < end && (i.CompletedAt == null || i.CompletedAt > start))
            .OrderBy(i => i.StartedAt).Take(limit)
            .Select(i => new { i.StartedAt, i.CompletedAt, i.Definition.Name, i.Definition.Category })
            .ToListAsync(cancellationToken);
        var now = (request.Now ?? request.To).ToUnixTimeMilliseconds();
        return [.. instances.Select(i =>
        {
            var started = Mills(i.StartedAt);
            var ended = i.CompletedAt is { } c ? Mills(c) : (long?)null;
            var days = ((ended ?? now) - started) / 86_400_000.0;
            var label = ended is null
                ? $"{i.Name}, {ReportFacts.Number(days)} days so far"
                : $"{i.Name}, {ReportFacts.Number(days)} days";
            return new Span(CategoryWords(i.Category), label, started, ended ?? to, ChartColorMapper.FromTracker(i.Category));
        })];
    }

    internal static string CategoryWords(TrackerCategory category) => category switch
    {
        TrackerCategory.Sensor => "Sensor",
        TrackerCategory.Cannula => "Cannula",
        TrackerCategory.Pod => "Pod",
        TrackerCategory.Reservoir => "Reservoir",
        TrackerCategory.Battery => "Battery",
        TrackerCategory.Consumable => "Consumable",
        TrackerCategory.Appointment => "Appointment",
        TrackerCategory.Reminder => "Reminder",
        _ => "Other",
    };
}
