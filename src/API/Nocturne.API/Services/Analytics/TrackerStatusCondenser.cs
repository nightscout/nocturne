using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Widget;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// Condenses active tracker instances into the <see cref="V4TrackerStatus"/> list the widget summary
/// and the cross-tenant overview serve. An instance whose definition is not loaded, or whose
/// dashboard visibility hides it at its current urgency, is left out.
/// </summary>
public static class TrackerStatusCondenser
{
    /// <summary>A running tracker as the overview condenses it, with what the dashboard filter and the flag facts read.</summary>
    /// <param name="HoursLeft">
    /// Hours to <see cref="TrackerInstance.ExpectedEndAt"/>, negative once past it; null for a tracker with no end.
    /// </param>
    public sealed record TrackerStatusNow(V4TrackerStatus Status, DashboardVisibility Visibility, double? HoursLeft);

    public static List<V4TrackerStatus> Condense(IEnumerable<TrackerInstance> instances, DateTime nowUtc) =>
        [.. instances.Select(i => StatusOf(i, nowUtc)).OfType<TrackerStatusNow>()
            .Where(s => ShouldShow(s.Visibility, s.Status.Urgency)).Select(s => s.Status)];

    /// <summary>Every running tracker of <paramref name="instances"/> at <paramref name="nowUtc"/>, whatever the dashboard shows.</summary>
    public static List<TrackerStatusNow> All(IEnumerable<TrackerInstance> instances, DateTime nowUtc) =>
        [.. instances.Select(i => StatusOf(i, nowUtc)).OfType<TrackerStatusNow>()];

    private static TrackerStatusNow? StatusOf(TrackerInstance instance, DateTime nowUtc)
    {
        if (instance.Definition == null)
        {
            return null;
        }

        var definition = instance.Definition;
        var urgency = TrackerSchedule.UrgencyAt(instance, nowUtc);

        var status = new V4TrackerStatus
        {
            Id = instance.Id,
            DefinitionId = definition.Id,
            Name = definition.Name,
            Icon = definition.Icon,
            Category = definition.Category,
            Mode = definition.Mode,
            Urgency = urgency,
            LifespanHours = definition.LifespanHours,
        };

        if (definition.Mode == TrackerMode.Duration)
        {
            status.AgeHours = Math.Round(instance.AgeHoursAt(nowUtc) * 10) / 10;

            if (definition.LifespanHours.HasValue && definition.LifespanHours.Value > 0)
            {
                status.PercentElapsed = Math.Round(
                    (instance.AgeHoursAt(nowUtc) / definition.LifespanHours.Value) * 100
                );
            }
        }
        else if (definition.Mode == TrackerMode.Event && instance.ScheduledAt.HasValue)
        {
            var hoursUntil = (instance.ScheduledAt.Value - nowUtc).TotalHours;
            status.HoursUntilEvent = Math.Round(hoursUntil * 10) / 10;
        }

        var hoursLeft = instance.ExpectedEndAt is { } end ? Math.Round((end - nowUtc).TotalHours * 10) / 10 : (double?)null;
        return new TrackerStatusNow(status, definition.DashboardVisibility, hoursLeft);
    }

    private static bool ShouldShow(DashboardVisibility visibility, NotificationUrgency currentUrgency)
    {
        return visibility switch
        {
            DashboardVisibility.Off => false,
            DashboardVisibility.Always => true,
            DashboardVisibility.Info => currentUrgency >= NotificationUrgency.Info,
            DashboardVisibility.Warn => currentUrgency >= NotificationUrgency.Warn,
            DashboardVisibility.Hazard => currentUrgency >= NotificationUrgency.Hazard,
            DashboardVisibility.Urgent => currentUrgency >= NotificationUrgency.Urgent,
            _ => true,
        };
    }
}
