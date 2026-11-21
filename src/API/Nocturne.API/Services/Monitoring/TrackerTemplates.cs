using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Monitoring;

/// <summary>
/// A ready-made tracker: the draft the tracker wizard starts from. A template is where a definition
/// starts and nothing ties a definition to it afterwards. It is a draft, not a definition, since the
/// person names the tracker and a definition always has a name.
/// </summary>
/// <param name="Id">The template's stable key; the wizard has its name and description under it.</param>
public sealed record TrackerTemplate(string Id, TrackerDefinitionDraft Draft);

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
    /// The templates for a tenant with <paramref name="devices"/>. A
    /// consumable is the one its registered device calls for (<see cref="ConsumableTrackerSpecs.For"/>),
    /// so a pod stands in for the infusion site and no reservoir or pump battery is offered beside it.
    /// </summary>
    public static IReadOnlyList<TrackerTemplate> For(IReadOnlyCollection<PatientDevice> devices)
    {
        var templates = new List<TrackerTemplate>();

        void Add(string id, ConsumableTrackerSpec? spec)
        {
            if (spec is not null)
                templates.Add(new(id, ConsumableTrackerSpecs.ToDraft(spec)));
        }

        Add(Sensor, ConsumableTrackerSpecs.For(LegacyConsumableKind.Sensor, devices));

        var site = ConsumableTrackerSpecs.For(LegacyConsumableKind.Site, devices);
        var siteIsPod = site?.Category == TrackerCategory.Pod;
        Add(siteIsPod ? Pod : Site, site);
        if (!siteIsPod)
            Add(Pod, ConsumableTrackerSpecs.TypicalPod);

        Add(Reservoir, ConsumableTrackerSpecs.For(LegacyConsumableKind.Reservoir, devices));
        Add(PumpBattery, ConsumableTrackerSpecs.For(LegacyConsumableKind.PumpBattery, devices));

        templates.Add(new(Appointment, new TrackerDefinitionDraft
        {
            Category = TrackerCategory.Appointment,
            Icon = "calendar",
            Mode = TrackerMode.Event,
            NotificationThresholds =
            [
                new(Guid.Empty, NotificationUrgency.Info, -AppointmentReminderHours, null, DisplayOrder: 0),
            ],
        }));
        templates.Add(new(Reminder, new TrackerDefinitionDraft
        {
            Category = TrackerCategory.Reminder,
            Icon = "bell",
            Mode = TrackerMode.Duration,
            LifespanHours = ReminderLifespanHours,
            NotificationThresholds =
            [
                new(Guid.Empty, NotificationUrgency.Warn, ReminderLifespanHours, null, DisplayOrder: 0),
            ],
        }));
        templates.Add(new(Custom, new TrackerDefinitionDraft
        {
            Category = TrackerCategory.Custom,
            Mode = TrackerMode.Duration,
        }));

        return templates;
    }
}
