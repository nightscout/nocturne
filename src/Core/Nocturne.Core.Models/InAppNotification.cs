namespace Nocturne.Core.Models;

/// <summary>
/// A stored in-app notification: what a user was told, how it can be acted on, when it resolves
/// itself and whether it was read or archived. <see cref="InAppNotificationDto"/> is the part of it
/// a client is sent.
/// </summary>
/// <seealso cref="NotificationCategory"/>
/// <seealso cref="NotificationUrgency"/>
/// <seealso cref="NotificationArchiveReason"/>
public sealed record InAppNotification
{
    public Guid Id { get; init; }

    /// <summary>The subject the notification is for.</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>Dotted string type for categorisation and handling (e.g. "tracker.alert").</summary>
    public string Type { get; init; } = string.Empty;

    public NotificationCategory Category { get; init; }

    public NotificationUrgency Urgency { get; init; }

    /// <summary>Lucide icon name that overrides the category's.</summary>
    public string? Icon { get; init; }

    /// <summary>The subsystem that created the notification.</summary>
    public string? Source { get; init; }

    public string Title { get; init; } = string.Empty;

    public string? Subtitle { get; init; }

    public DateTime CreatedAt { get; init; }

    /// <summary>The entity the notification is about, which groups it and lets that entity resolve it.</summary>
    public string? SourceId { get; init; }

    public IReadOnlyList<NotificationActionDto> Actions { get; init; } = [];

    /// <summary>When the notification archives itself; null when it waits for the user.</summary>
    public ResolutionConditions? ResolutionConditions { get; init; }

    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>When the user read it, or null while unread. Read is independent of <see cref="IsArchived"/>.</summary>
    public DateTime? ReadAt { get; init; }

    public bool IsArchived { get; init; }

    public DateTime? ArchivedAt { get; init; }

    public NotificationArchiveReason? ArchiveReason { get; init; }

    public InAppNotificationDto ToDto() => new()
    {
        Id = Id,
        Type = Type,
        Category = Category,
        Icon = Icon,
        Source = Source,
        Urgency = Urgency,
        Title = Title,
        Subtitle = Subtitle,
        CreatedAt = CreatedAt,
        ReadAt = ReadAt,
        SourceId = SourceId,
        Metadata = Metadata,
        Actions = [.. Actions],
    };
}
