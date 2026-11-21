using Nocturne.Core.Models;
using Nocturne.Core.Models.Widget;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// Condenses active tracker instances into the <see cref="V4TrackerStatus"/> list the widget summary
/// and the cross-tenant overview serve. An instance whose definition is not loaded, or whose
/// dashboard visibility hides it at its current urgency, is left out.
/// </summary>
public static class TrackerStatusCondenser
{
    public static List<V4TrackerStatus> Condense(IEnumerable<TrackerInstanceEntity> instances, DateTime nowUtc)
    {
        var statuses = new List<V4TrackerStatus>();

        foreach (var instance in instances)
        {
            if (instance.Definition == null)
            {
                continue;
            }

            var definition = instance.Definition;
            var urgency = CalculateUrgency(instance, definition, nowUtc);

            if (!ShouldShow(definition.DashboardVisibility, urgency))
            {
                continue;
            }

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
                status.AgeHours = Math.Round(instance.AgeHours * 10) / 10;

                if (definition.LifespanHours.HasValue && definition.LifespanHours.Value > 0)
                {
                    status.PercentElapsed = Math.Round(
                        (instance.AgeHours / definition.LifespanHours.Value) * 100
                    );
                }
            }
            else if (definition.Mode == TrackerMode.Event && instance.ScheduledAt.HasValue)
            {
                var hoursUntil = (instance.ScheduledAt.Value - nowUtc).TotalHours;
                status.HoursUntilEvent = Math.Round(hoursUntil * 10) / 10;
            }

            statuses.Add(status);
        }

        return statuses;
    }

    private static NotificationUrgency CalculateUrgency(
        TrackerInstanceEntity instance,
        TrackerDefinitionEntity definition,
        DateTime nowUtc
    )
    {
        var thresholds = definition.NotificationThresholds
            .OrderByDescending(t => t.Urgency)
            .ToList();

        if (!thresholds.Any())
        {
            return NotificationUrgency.Info;
        }

        if (definition.Mode == TrackerMode.Duration)
        {
            var ageHours = instance.AgeHours;
            var lifespanHours = definition.LifespanHours ?? 0;

            foreach (var threshold in thresholds)
            {
                // Negative hours count back from the end of the lifespan.
                var effectiveThresholdHours = threshold.Hours < 0
                    ? lifespanHours + threshold.Hours
                    : threshold.Hours;

                if (ageHours >= effectiveThresholdHours)
                {
                    return threshold.Urgency;
                }
            }
        }
        else if (definition.Mode == TrackerMode.Event && instance.ScheduledAt.HasValue)
        {
            var hoursUntilEvent = (instance.ScheduledAt.Value - nowUtc).TotalHours;

            foreach (var threshold in thresholds)
            {
                // Negative hours fire when approaching the event, positive hours when overdue.
                if (threshold.Hours < 0)
                {
                    if (hoursUntilEvent <= Math.Abs(threshold.Hours) && hoursUntilEvent >= 0)
                    {
                        return threshold.Urgency;
                    }
                }
                else if (hoursUntilEvent < 0 && Math.Abs(hoursUntilEvent) >= threshold.Hours)
                {
                    return threshold.Urgency;
                }
            }
        }

        return NotificationUrgency.Info;
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
