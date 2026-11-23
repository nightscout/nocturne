namespace Nocturne.Core.Models;

/// <summary>One run of a <see cref="TrackerDefinition"/>: running until <see cref="CompletedAt"/> is set.</summary>
public sealed record TrackerInstance
{
    public Guid Id { get; init; }

    /// <summary>The subject who started the run.</summary>
    public string CreatedBy { get; init; } = string.Empty;

    public required TrackerDefinition Definition { get; init; }

    public Guid DefinitionId => Definition.Id;

    public DateTime StartedAt { get; init; }

    /// <summary>
    /// When this run was recorded. A run is often entered after the fact, and a notification
    /// threshold already due by then does not alert for it. Null on a run recorded before the time
    /// was kept, which holds nothing back.
    /// </summary>
    public DateTime? RecordedAt { get; init; }

    public DateTime? CompletedAt { get; init; }

    /// <summary>For an Event tracker: when the event is.</summary>
    public DateTime? ScheduledAt { get; init; }

    /// <summary>The device event that started this run.</summary>
    public string? StartTreatmentId { get; init; }

    /// <summary>The device event that completed this run.</summary>
    public string? CompleteTreatmentId { get; init; }

    public string? StartNotes { get; init; }
    public string? CompletionNotes { get; init; }
    public CompletionReason? CompletionReason { get; init; }
    public DateTime? LastAckedAt { get; init; }
    public int? AckSnoozeMins { get; init; }

    /// <summary>When the run is due to end: the scheduled time of an Event tracker, else the end of its lifespan.</summary>
    public DateTime? ExpectedEndAt =>
        Definition.Mode == TrackerMode.Event
            ? ScheduledAt
            : Definition.LifespanHours is { } lifespan
                ? StartedAt.AddHours(lifespan)
                : null;

    public bool IsActive => CompletedAt == null;

    /// <summary>Hours from the start to <paramref name="now"/>.</summary>
    public double AgeHoursAt(DateTime now) => (now - StartedAt).TotalHours;
}

/// <summary>A saved way to start a run of a definition, with notes to start it with.</summary>
public sealed record TrackerPreset
{
    public Guid Id { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public Guid DefinitionId { get; init; }
    public string? DefaultStartNotes { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>The definition it starts, without its notification thresholds. Null when it was not read with the preset.</summary>
    public TrackerDefinition? Definition { get; init; }
}
