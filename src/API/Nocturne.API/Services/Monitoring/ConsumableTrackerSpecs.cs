using System.Text.Json;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Monitoring;

/// <summary>A Duration tracker definition for a piece of hardware that wears out.</summary>
/// <param name="Name">The name the tracker definition is created with.</param>
/// <param name="Category">The tracker category it is filed under.</param>
/// <param name="Icon">The name of the icon it is shown with.</param>
/// <param name="LifespanHours">Hours the hardware is expected to last, at which its hazard threshold falls.</param>
/// <param name="Triggers">The device event types, in their canonical spelling, that restart it.</param>
/// <param name="Ends">The device event types, in their canonical spelling, that end it.</param>
/// <param name="Covers">What the hardware it follows provides; empty when that is not known.</param>
/// <param name="Component">The <see cref="ConsumableTrackerSpecs.ComponentRef"/> of the pump part it follows.</param>
/// <param name="GraceHours">Hours past the lifespan the hardware keeps working before it stops.</param>
public sealed record ConsumableTrackerSpec(
    string Name,
    TrackerCategory Category,
    string Icon,
    int LifespanHours,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string>? Ends = null,
    IReadOnlyList<PumpCapability>? Covers = null,
    string? Component = null,
    int? GraceHours = null);

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
            [DeviceEventType.SiteChange, DeviceEventType.CannulaChange, DeviceEventType.PodChange,
                DeviceEventType.PodActivated],
        LegacyConsumableKind.Reservoir => [DeviceEventType.InsulinChange, DeviceEventType.ReservoirChange],
        LegacyConsumableKind.PumpBattery => [DeviceEventType.PumpBatteryChange],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The pump capability <paramref name="kind"/> ages, or null for a CGM sensor.</summary>
    public static PumpCapability? CapabilityOf(LegacyConsumableKind kind) => kind switch
    {
        LegacyConsumableKind.Site => PumpCapability.Site,
        LegacyConsumableKind.Reservoir => PumpCapability.Reservoir,
        LegacyConsumableKind.PumpBattery => PumpCapability.Power,
        _ => null,
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
            [TreatmentTypes.SiteChange, TreatmentTypes.CannulaChange, TreatmentTypes.PodChange,
                TreatmentTypes.PodActivated],
            [TreatmentTypes.PodDeactivated],
            Covers: [PumpCapability.Site]),
        LegacyConsumableKind.Reservoir => new("Reservoir", TrackerCategory.Reservoir, "cylinder", 72,
            [TreatmentTypes.InsulinChange, TreatmentTypes.ReservoirChangeEvent],
            Covers: [PumpCapability.Reservoir]),
        LegacyConsumableKind.PumpBattery => new("Pump Battery", TrackerCategory.Battery, "battery", 504,
            [TreatmentTypes.PumpBatteryChange],
            Covers: [PumpCapability.Power]),
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
                [TreatmentTypes.SensorStart, TreatmentTypes.SensorChange],
                GraceHours: cgm.GraceHours));

            if (cgm is { HasSeparateTransmitter: true, TransmitterDurationDays: { } transmitterDays })
                specs.Add(new($"{entry.Name} transmitter", TrackerCategory.Battery, "battery",
                    transmitterDays * 24, []));
        }

        if (entry.Pump is { } pump)
            specs.AddRange(pump.Components
                .Where(c => c.TrackedByDefault)
                .Select(c => SpecFor(entry, c))
                .OfType<ConsumableTrackerSpec>());

        return specs;
    }

    /// <summary>
    /// The tracker that follows <paramref name="component"/> of <paramref name="entry"/>'s pump, or
    /// null for a part with no lifespan.
    /// </summary>
    public static ConsumableTrackerSpec? SpecFor(DeviceCatalogEntry entry, PumpComponent component)
    {
        if (component.LifespanHours is not { } hours)
            return null;
        var (category, icon) = Presentation(component.Kind);
        return new ConsumableTrackerSpec(
            $"{entry.Name} {NameOf(component.Kind)}",
            category,
            icon,
            hours,
            [.. component.ResetBy.Select(CanonicalName)],
            [.. component.EndedBy.Select(CanonicalName)],
            component.Provides,
            ComponentRef(entry, component),
            component.GraceHours);
    }

    /// <summary>
    /// How a tracker names the pump part it follows: the catalog id and the part's key, e.g.
    /// "omnipod-5/pod".
    /// </summary>
    public static string ComponentRef(DeviceCatalogEntry entry, PumpComponent component) =>
        $"{entry.Id}/{component.Key}";

    /// <summary>The tracker category and icon a part of <paramref name="kind"/> is filed under.</summary>
    public static (TrackerCategory Category, string Icon) Presentation(PumpComponentKind kind) => kind switch
    {
        PumpComponentKind.Pod => (TrackerCategory.Pod, "hexagon"),
        PumpComponentKind.InfusionSet => (TrackerCategory.Cannula, "syringe"),
        PumpComponentKind.Reservoir => (TrackerCategory.Reservoir, "cylinder"),
        PumpComponentKind.Base or PumpComponentKind.Controller => (TrackerCategory.Consumable, "cpu"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string NameOf(PumpComponentKind kind) => kind switch
    {
        PumpComponentKind.Pod => "pod",
        PumpComponentKind.InfusionSet => "infusion site",
        PumpComponentKind.Reservoir => "reservoir",
        PumpComponentKind.Base => "pump base",
        PumpComponentKind.Controller => "controller",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The spelling of <paramref name="type"/> a definition's trigger or end list stores.</summary>
    private static string CanonicalName(DeviceEventType type) =>
        TreatmentTypes.DeviceEventTypeMap.First(pair => pair.Value == type).Key;

    /// <summary>
    /// The spec for <paramref name="kind"/>: the first, by rank, of the specs of
    /// <paramref name="devices"/>' catalog entries that <see cref="Serves"/> the kind and is that
    /// kind's own, else <see cref="Generic"/>. Null when the only specs serving it are another
    /// kind's: a pod's insulin and battery are aged with it, as the site.
    /// </summary>
    public static ConsumableTrackerSpec? For(LegacyConsumableKind kind, IEnumerable<PatientDevice> devices)
    {
        var serving = devices
            .OrderBy(d => d.Rank ?? int.MaxValue)
            .Select(d => d.CatalogId is { } id ? DeviceCatalog.GetById(id) : null)
            .OfType<DeviceCatalogEntry>()
            .SelectMany(SpecsFor)
            .Where(spec => Serves(spec, kind))
            .ToList();
        if (serving.FirstOrDefault(spec => KindOf(spec) == kind) is { } own)
            return own;
        return serving.Count == 0 ? Generic(kind) : null;
    }

    /// <summary>
    /// Whether <paramref name="spec"/> answers for <paramref name="kind"/>: its hardware provides the
    /// kind's capability, or, when what it provides is not known, one of the kind's events restarts it.
    /// </summary>
    public static bool Serves(ConsumableTrackerSpec spec, LegacyConsumableKind kind)
    {
        if (spec.Covers is { Count: > 0 } covers)
            return CapabilityOf(kind) is { } capability && covers.Contains(capability);
        var kindEvents = EventTypes(kind);
        return TriggerEventTypes(spec).Any(kindEvents.Contains);
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
    /// the lifespan ends, a hazard when it has, and for hardware with a grace period, an urgent
    /// alert <see cref="UrgentHoursBeforeStop"/> before it stops.
    /// </summary>
    public static IReadOnlyList<TrackerNotificationThresholdEntity> Thresholds(ConsumableTrackerSpec spec)
    {
        List<TrackerNotificationThresholdEntity> thresholds =
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
        if (spec.GraceHours is { } grace && grace > UrgentHoursBeforeStop)
            thresholds.Add(new()
            {
                Urgency = NotificationUrgency.Urgent,
                Hours = spec.LifespanHours + grace - UrgentHoursBeforeStop,
                DisplayOrder = 2,
            });
        return thresholds;
    }

    /// <summary>How long before hardware stops at the end of its grace period the urgent alert fires.</summary>
    public const int UrgentHoursBeforeStop = 1;

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
            EndEventTypes = JsonSerializer.Serialize(spec.Ends ?? []),
            Covers = TrackerCoverage.Serialize(spec.Covers ?? []),
            DeviceComponent = spec.Component,
            StartEventType = spec.Triggers.FirstOrDefault(),
            Mode = TrackerMode.Duration,
        };
        if (withThresholds)
            foreach (var threshold in Thresholds(spec))
                definition.NotificationThresholds.Add(threshold);
        return definition;
    }
}
