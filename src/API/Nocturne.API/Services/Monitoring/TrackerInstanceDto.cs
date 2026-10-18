using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

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

    public static TrackerInstanceDto FromEntity(TrackerInstanceEntity entity) =>
        new()
        {
            Id = entity.Id,
            DefinitionId = entity.DefinitionId,
            DefinitionName = entity.Definition?.Name ?? string.Empty,
            Category = entity.Definition?.Category ?? TrackerCategory.Custom,
            Icon = entity.Definition?.Icon ?? "activity",
            StartedAt = entity.StartedAt,
            CompletedAt = entity.CompletedAt,
            ExpectedEndAt = entity.ExpectedEndAt,
            ScheduledAt = entity.ScheduledAt,
            StartNotes = entity.StartNotes,
            CompletionNotes = entity.CompletionNotes,
            CompletionReason = entity.CompletionReason,
            AgeHours = entity.AgeHours,
            IsActive = entity.IsActive,
            LastAckedAt = entity.LastAckedAt,
            AckSnoozeMins = entity.AckSnoozeMins,
            Schedule = entity.Definition is null
                ? []
                : [.. entity.Definition.NotificationThresholds
                    .OrderBy(t => t.DisplayOrder)
                    .Select(t => (Threshold: t, FiresAt: TrackerSchedule.FiresAt(entity, t)))
                    .Where(t => t.FiresAt is not null)
                    .Select(t => new TrackerThresholdTimeDto
                    {
                        Urgency = t.Threshold.Urgency,
                        FiresAt = t.FiresAt!.Value,
                        Description = t.Threshold.Description,
                    })],
        };
}

/// <summary>One notification threshold of a tracker run, placed on the timeline.</summary>
public class TrackerThresholdTimeDto
{
    public NotificationUrgency Urgency { get; set; }
    public DateTime FiresAt { get; set; }
    public string? Description { get; set; }
}
