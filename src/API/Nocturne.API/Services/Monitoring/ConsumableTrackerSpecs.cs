using System.Text.Json;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Monitoring;

/// <summary>A Duration tracker definition for a piece of hardware that wears out.</summary>
/// <param name="Triggers">The device event types, in their canonical spelling, that restart it.</param>
public sealed record ConsumableTrackerSpec(
    string Name,
    TrackerCategory Category,
    string Icon,
    int LifespanHours,
    IReadOnlyList<string> Triggers);

/// <summary>
/// The trackers consumable hardware calls for: those a device's <see cref="DeviceCatalogEntry"/>
/// implies, which <c>DeviceTrackerProvisioner</c> creates on registration and the legacy fallbacks
/// prefer, and a generic one per <see cref="LegacyConsumableKind"/> for a tenant with no matching
/// device.
/// </summary>
public static class ConsumableTrackerSpecs
{
    /// <summary>
    /// The device events that renew each kind. Nocturne's own sets, so a pod change renews the site.
    /// </summary>
    public static IReadOnlyList<DeviceEventType> EventTypes(LegacyConsumableKind kind) => kind switch
    {
        LegacyConsumableKind.Sensor => [DeviceEventType.SensorStart, DeviceEventType.SensorChange],
        LegacyConsumableKind.Site =>
            [DeviceEventType.SiteChange, DeviceEventType.CannulaChange, DeviceEventType.PodChange],
        LegacyConsumableKind.Reservoir => [DeviceEventType.InsulinChange, DeviceEventType.ReservoirChange],
        LegacyConsumableKind.PumpBattery => [DeviceEventType.PumpBatteryChange],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// The spec for a kind when no registered device supplies one. Lifespans are typical wear
    /// times: a 10-day CGM sensor, a 3-day infusion site and reservoir, a 3-week pump battery.
    /// </summary>
    public static ConsumableTrackerSpec Generic(LegacyConsumableKind kind) => kind switch
    {
        LegacyConsumableKind.Sensor => new("CGM Sensor", TrackerCategory.Sensor, "radio", 240,
            [TreatmentTypes.SensorStart, TreatmentTypes.SensorChange]),
        LegacyConsumableKind.Site => new("Infusion Site", TrackerCategory.Cannula, "syringe", 72,
            [TreatmentTypes.SiteChange, TreatmentTypes.CannulaChange, TreatmentTypes.PodChange]),
        LegacyConsumableKind.Reservoir => new("Reservoir", TrackerCategory.Reservoir, "cylinder", 72,
            [TreatmentTypes.InsulinChange, TreatmentTypes.ReservoirChangeEvent]),
        LegacyConsumableKind.PumpBattery => new("Pump Battery", TrackerCategory.Battery, "battery", 504,
            [TreatmentTypes.PumpBatteryChange]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The trackers <paramref name="entry"/> calls for, empty when it carries no wear data.</summary>
    public static IReadOnlyList<ConsumableTrackerSpec> SpecsFor(DeviceCatalogEntry entry)
    {
        var specs = new List<ConsumableTrackerSpec>();

        if (entry.Cgm is { } cgm)
        {
            specs.Add(new($"{entry.Name} sensor", TrackerCategory.Sensor, "radio",
                cgm.SensorDurationDays * 24,
                [TreatmentTypes.SensorStart, TreatmentTypes.SensorChange]));

            if (cgm is { HasSeparateTransmitter: true, TransmitterDurationDays: { } transmitterDays })
                specs.Add(new($"{entry.Name} transmitter", TrackerCategory.Battery, "battery",
                    transmitterDays * 24, []));
        }

        if (entry.Pump is { } pump)
        {
            if (pump.IsPatch)
            {
                specs.Add(new($"{entry.Name} pod", TrackerCategory.Cannula, "syringe",
                    pump.SiteDurationHours,
                    [TreatmentTypes.PodChange, TreatmentTypes.SiteChange]));
            }
            else
            {
                specs.Add(new($"{entry.Name} infusion site", TrackerCategory.Cannula, "syringe",
                    pump.SiteDurationHours,
                    [TreatmentTypes.SiteChange, TreatmentTypes.CannulaChange]));

                if (pump.ReservoirDurationHours is { } reservoirHours)
                    specs.Add(new($"{entry.Name} reservoir", TrackerCategory.Reservoir, "cylinder",
                        reservoirHours,
                        [TreatmentTypes.InsulinChange, TreatmentTypes.ReservoirChangeEvent]));
            }
        }

        return specs;
    }

    /// <summary>
    /// The spec for <paramref name="kind"/>: the first, by rank, of <paramref name="devices"/> whose
    /// catalog entry has a spec restarted by one of the kind's events, else <see cref="Generic"/>.
    /// Null for a reservoir when a registered pump is a patch pump, whose insulin is in the pod.
    /// </summary>
    public static ConsumableTrackerSpec? For(LegacyConsumableKind kind, IEnumerable<PatientDevice> devices)
    {
        var kindEvents = EventTypes(kind);
        var entries = devices
            .OrderBy(d => d.Rank ?? int.MaxValue)
            .Select(d => d.CatalogId is { } id ? DeviceCatalog.GetById(id) : null)
            .OfType<DeviceCatalogEntry>()
            .ToList();
        var fromCatalog = entries
            .SelectMany(SpecsFor)
            .FirstOrDefault(spec => TriggerEventTypes(spec).Any(kindEvents.Contains));
        if (fromCatalog is not null)
            return fromCatalog;
        if (kind == LegacyConsumableKind.Reservoir && entries.Any(e => e.Pump is { IsPatch: true }))
            return null;
        return Generic(kind);
    }

    /// <summary>The category of the trackers that stand for <paramref name="kind"/>.</summary>
    public static TrackerCategory CategoryOf(LegacyConsumableKind kind) => kind switch
    {
        LegacyConsumableKind.Sensor => TrackerCategory.Sensor,
        LegacyConsumableKind.Site => TrackerCategory.Cannula,
        LegacyConsumableKind.Reservoir => TrackerCategory.Reservoir,
        LegacyConsumableKind.PumpBattery => TrackerCategory.Battery,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The kind <paramref name="spec"/> is restarted as, or null for one no event restarts.</summary>
    public static LegacyConsumableKind? KindOf(ConsumableTrackerSpec spec)
    {
        var triggers = TriggerEventTypes(spec);
        return Enum.GetValues<LegacyConsumableKind>()
            .Select(kind => (LegacyConsumableKind?)kind)
            .FirstOrDefault(kind => EventTypes(kind!.Value).Any(triggers.Contains));
    }

    /// <summary>The typed device events that restart <paramref name="spec"/>.</summary>
    public static IReadOnlySet<DeviceEventType> TriggerEventTypes(ConsumableTrackerSpec spec) =>
        TrackerTriggerService.ParseTriggerEventTypes(spec.Triggers);

    /// <summary>
    /// The notification ladder of every consumable tracker Nocturne creates: a warning a day before
    /// the lifespan ends, and a hazard when it has.
    /// </summary>
    public static IReadOnlyList<TrackerNotificationThresholdEntity> Thresholds(ConsumableTrackerSpec spec) =>
    [
        new()
        {
            Urgency = NotificationUrgency.Warn,
            Hours = -WarnHoursBeforeEnd,
            DisplayOrder = 0,
        },
        new()
        {
            Urgency = NotificationUrgency.Hazard,
            Hours = spec.LifespanHours,
            DisplayOrder = 1,
        },
    ];

    /// <summary>How long before a consumable's lifespan ends its warning fires.</summary>
    public const int WarnHoursBeforeEnd = 24;

    /// <summary>
    /// An unsaved Duration definition of <paramref name="spec"/>, created by <paramref name="userId"/>,
    /// with its <see cref="Thresholds"/> unless <paramref name="withThresholds"/> is false.
    /// </summary>
    public static TrackerDefinitionEntity ToDefinition(
        ConsumableTrackerSpec spec, string userId, bool withThresholds = true)
    {
        var definition = new TrackerDefinitionEntity
        {
            CreatedBy = userId,
            Name = spec.Name,
            Category = spec.Category,
            Icon = spec.Icon,
            LifespanHours = spec.LifespanHours,
            TriggerEventTypes = JsonSerializer.Serialize(spec.Triggers),
            StartEventType = spec.Triggers.FirstOrDefault(),
            Mode = TrackerMode.Duration,
        };
        if (withThresholds)
            foreach (var threshold in Thresholds(spec))
                definition.NotificationThresholds.Add(threshold);
        return definition;
    }
}
