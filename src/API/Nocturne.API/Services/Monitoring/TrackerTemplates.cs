using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Monitoring;

/// <summary>
/// A ready-made tracker: an unsaved definition the tracker wizard starts from. A template is where a
/// definition starts and nothing ties a definition to it afterwards.
/// </summary>
/// <param name="Id">The template's stable key; the wizard has its name and description under it.</param>
public sealed record TrackerTemplate(string Id, TrackerDefinitionEntity Definition);

/// <summary>The trackers the wizard offers, in the order they are offered.</summary>
public static class TrackerTemplates
{
    public const string Sensor = "sensor";
    public const string Site = "site";
    public const string Pod = "pod";
    public const string Reservoir = "reservoir";
    public const string PumpBattery = "pump_battery";
    public const string Appointment = "appointment";
    public const string Reminder = "reminder";
    public const string Custom = "custom";

    /// <summary>Hours before an appointment its one reminder fires.</summary>
    public const int AppointmentReminderHours = 24;

    /// <summary>The lifespan a reminder starts with: 30 days.</summary>
    public const int ReminderLifespanHours = 30 * 24;

    /// <summary>
    /// The templates for a tenant with <paramref name="devices"/>. A consumable is the one its
    /// registered device calls for (<see cref="ConsumableTrackerSpecs.For"/>), so a pod stands in
    /// for the infusion site and no reservoir or pump battery is offered beside it.
    /// </summary>
    public static IReadOnlyList<TrackerTemplate> For(IReadOnlyCollection<PatientDevice> devices)
    {
        var templates = new List<TrackerTemplate>();

        void Add(string id, ConsumableTrackerSpec? spec)
        {
            if (spec is not null)
                templates.Add(new(id, ConsumableTrackerSpecs.ToDefinition(spec, string.Empty)));
        }

        Add(Sensor, ConsumableTrackerSpecs.For(LegacyConsumableKind.Sensor, devices));

        var site = ConsumableTrackerSpecs.For(LegacyConsumableKind.Site, devices);
        var siteIsPod = site?.Category == TrackerCategory.Pod;
        Add(siteIsPod ? Pod : Site, site);
        if (!siteIsPod)
            Add(Pod, ConsumableTrackerSpecs.TypicalPod);

        Add(Reservoir, ConsumableTrackerSpecs.For(LegacyConsumableKind.Reservoir, devices));
        Add(PumpBattery, ConsumableTrackerSpecs.For(LegacyConsumableKind.PumpBattery, devices));

        templates.Add(new(Appointment, new TrackerDefinitionEntity
        {
            Category = TrackerCategory.Appointment,
            Icon = "calendar",
            Mode = TrackerMode.Event,
            NotificationThresholds =
            [
                new() { Urgency = NotificationUrgency.Info, Hours = -AppointmentReminderHours, DisplayOrder = 0 },
            ],
        }));
        templates.Add(new(Reminder, new TrackerDefinitionEntity
        {
            Category = TrackerCategory.Reminder,
            Icon = "bell",
            Mode = TrackerMode.Duration,
            LifespanHours = ReminderLifespanHours,
            NotificationThresholds =
            [
                new() { Urgency = NotificationUrgency.Warn, Hours = ReminderLifespanHours, DisplayOrder = 0 },
            ],
        }));
        templates.Add(new(Custom, new TrackerDefinitionEntity
        {
            Category = TrackerCategory.Custom,
            Mode = TrackerMode.Duration,
        }));

        return templates;
    }
}
