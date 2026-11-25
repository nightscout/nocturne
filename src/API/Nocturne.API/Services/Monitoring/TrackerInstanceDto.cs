using Nocturne.Core.Models;

namespace Nocturne.API.Services.Monitoring;

public class TrackerInstanceDto
{
    public Guid Id { get; set; }
    public Guid DefinitionId { get; set; }
    public string DefinitionName { get; set; } = string.Empty;
    public TrackerCategory Category { get; set; }
    public string Icon { get; set; } = "activity";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ExpectedEndAt { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public string? StartNotes { get; set; }
    public string? CompletionNotes { get; set; }
    public CompletionReason? CompletionReason { get; set; }
    public double AgeHours { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastAckedAt { get; set; }
    public int? AckSnoozeMins { get; set; }

    /// <summary>
    /// When each of the definition's notification thresholds fires for this run, resolved by
    /// <see cref="TrackerSchedule"/> with the rule the threshold's alert rule uses. The alert follows
    /// only a definition's newest run, so for an Event tracker with several bookings an older one's
    /// schedule has no alert behind it. A client finds the level a running tracker has reached by
    /// comparing these against the clock, never by re-deriving them from the thresholds' hours.
    /// Thresholds that can never fire are omitted.
    /// </summary>
    public TrackerThresholdTimeDto[] Schedule { get; set; } = [];

    /// <summary>The run as a client reads it, its age measured at <paramref name="now"/>.</summary>
    public static TrackerInstanceDto From(TrackerInstance instance, DateTime now) =>
        new()
        {
            Id = instance.Id,
            DefinitionId = instance.DefinitionId,
            DefinitionName = instance.Definition.Name,
            Category = instance.Definition.Category,
            Icon = instance.Definition.Icon,
            StartedAt = instance.StartedAt,
            CompletedAt = instance.CompletedAt,
            ExpectedEndAt = instance.ExpectedEndAt,
            ScheduledAt = instance.ScheduledAt,
            StartNotes = instance.StartNotes,
            CompletionNotes = instance.CompletionNotes,
            CompletionReason = instance.CompletionReason,
            AgeHours = instance.AgeHoursAt(now),
            IsActive = instance.IsActive,
            LastAckedAt = instance.LastAckedAt,
            AckSnoozeMins = instance.AckSnoozeMins,
            Schedule =
            [
                .. instance.Definition.NotificationThresholds
                    .OrderBy(t => t.DisplayOrder)
                    .Select(t => (Threshold: t, FiresAt: TrackerSchedule.FiresAt(instance, t)))
                    .Where(t => t.FiresAt is not null)
                    .Select(t => new TrackerThresholdTimeDto
                    {
                        Urgency = t.Threshold.Urgency,
                        FiresAt = t.FiresAt!.Value,
                        Description = t.Threshold.Description,
                    }),
            ],
        };
}

/// <summary>One notification threshold of a tracker run, placed on the timeline.</summary>
public class TrackerThresholdTimeDto
{
    public NotificationUrgency Urgency { get; set; }
    public DateTime FiresAt { get; set; }
    public string? Description { get; set; }
}
