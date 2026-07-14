using Nocturne.Core.Models;

namespace Nocturne.Services.Demo.Services;

/// <summary>A single device lifecycle change (Nightscout treatment EventType + UTC time).</summary>
public sealed record DeviceChangeEvent(string EventType, DateTime TimestampUtc);

/// <summary>
/// A tracker definition to seed alongside the device schedule. Instances are
/// derived from the <see cref="DeviceChangeEvent"/>s whose EventType matches
/// <see cref="TriggerEventType"/>: each event starts an instance and completes
/// the previous one, leaving the most recent instance running.
/// </summary>
public sealed record TrackerSeedSpec(
    string Name,
    TrackerCategory Category,
    string Icon,
    int LifespanHours,
    string TriggerEventType,
    CompletionReason CompletionReason);

/// <summary>
/// Deterministic device-change schedule (sensor, infusion site, reservoir, pump
/// battery). Emitted as treatments so the normal decomposer produces
/// <c>DeviceEvent</c>s — which is what drives the dashboard age pills
/// (SAGE/CAGE/IAGE/BAGE) and the site-change-impact report. Change days are
/// anchored to the calendar (day-number modulo cycle), so ages look realistic
/// at any seed time and re-seeding reproduces the same schedule.
/// </summary>
public static class DemoDeviceLifecycle
{
    private sealed record DeviceKind(TrackerSeedSpec Tracker, int CycleDays, int Phase, int HourOfDay);

    private static readonly DeviceKind[] Kinds =
    [
        new(new TrackerSeedSpec("CGM Sensor", TrackerCategory.Sensor, "radio", 240,
            "Sensor Start", CompletionReason.Completed), CycleDays: 10, Phase: 3, HourOfDay: 9),
        new(new TrackerSeedSpec("Infusion Site", TrackerCategory.Cannula, "syringe", 72,
            "Site Change", CompletionReason.Completed), CycleDays: 3, Phase: 1, HourOfDay: 8),
        new(new TrackerSeedSpec("Reservoir", TrackerCategory.Reservoir, "cylinder", 72,
            "Insulin Change", CompletionReason.Refilled), CycleDays: 3, Phase: 1, HourOfDay: 8),
        new(new TrackerSeedSpec("Pump Battery", TrackerCategory.Battery, "battery", 504,
            "Pump Battery Change", CompletionReason.Completed), CycleDays: 21, Phase: 8, HourOfDay: 19),
    ];

    /// <summary>Tracker definitions matching the generated schedule.</summary>
    public static IReadOnlyList<TrackerSeedSpec> TrackerSpecs { get; } =
        Kinds.Select(k => k.Tracker).ToList();

    /// <summary>
    /// All device changes in the window, chronological. One extra cycle before
    /// the window is included per kind so every age has a defined start even on
    /// short backfills. <paramref name="localToday"/> is a local midnight;
    /// timestamps are UTC.
    /// </summary>
    public static List<DeviceChangeEvent> GenerateSchedule(DateTime localToday, int backfillDays)
    {
        var events = new List<DeviceChangeEvent>();
        foreach (var kind in Kinds)
        {
            for (var d = backfillDays + kind.CycleDays; d >= 0; d--)
            {
                var day = localToday.AddDays(-d);
                var dayNumber = (int)(day.Ticks / TimeSpan.TicksPerDay);
                if ((dayNumber % kind.CycleDays + kind.CycleDays) % kind.CycleDays != kind.Phase)
                    continue;

                var jitter = DayScenarios.RngFor(day, $"device:{kind.Tracker.TriggerEventType}");
                var local = day.AddHours(kind.HourOfDay).AddMinutes(jitter.Next(-90, 90));
                if (local > DateTime.Now)
                    continue;
                events.Add(new DeviceChangeEvent(kind.Tracker.TriggerEventType, local.ToUniversalTime()));
            }
        }

        return events.OrderBy(e => e.TimestampUtc).ToList();
    }

    /// <summary>
    /// The change event as a Nightscout treatment; the treatment decomposer maps
    /// the EventType to the corresponding <c>DeviceEventType</c>.
    /// </summary>
    public static Treatment ToTreatment(DeviceChangeEvent change, string dataSource) => new()
    {
        EventType = change.EventType,
        Mills = new DateTimeOffset(change.TimestampUtc).ToUnixTimeMilliseconds(),
        Created_at = change.TimestampUtc.ToString("o"),
        EnteredBy = "demo-user",
        DataSource = dataSource,
    };
}
