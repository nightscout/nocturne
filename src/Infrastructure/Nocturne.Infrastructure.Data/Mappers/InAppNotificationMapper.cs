using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Stored in-app notifications as the Core model, and back. The actions, the resolution
/// conditions and the metadata are stored as JSON: a column that does not parse reads as none.
/// </summary>
public static class InAppNotificationMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static InAppNotification ToDomain(InAppNotificationEntity entity) => new()
    {
        Id = entity.Id,
        UserId = entity.UserId,
        Type = entity.Type,
        Category = entity.Category,
        Urgency = entity.Urgency,
        Icon = entity.Icon,
        Source = entity.Source,
        Title = entity.Title,
        Subtitle = entity.Subtitle,
        CreatedAt = entity.CreatedAt,
        SourceId = entity.SourceId,
        Actions = MapperHelpers.DeserializeJson<List<NotificationActionDto>>(entity.ActionsJson, JsonOptions) ?? [],
        ResolutionConditions = MapperHelpers.DeserializeJson<ResolutionConditions>(entity.ResolutionConditionsJson, JsonOptions),
        Metadata = MapperHelpers.DeserializeJson<Dictionary<string, object>>(entity.MetadataJson, JsonOptions),
        ReadAt = entity.ReadAt,
        IsArchived = entity.IsArchived,
        ArchivedAt = entity.ArchivedAt,
        ArchiveReason = entity.ArchiveReason,
    };

    /// <summary>A new row of <paramref name="notification"/>, its tenant left for the context to stamp.</summary>
    public static InAppNotificationEntity ToEntity(InAppNotification notification) => new()
    {
        Id = notification.Id,
        UserId = notification.UserId,
        Type = notification.Type,
        Category = notification.Category,
        Urgency = notification.Urgency,
        Icon = notification.Icon,
        Source = notification.Source,
        Title = notification.Title,
        Subtitle = notification.Subtitle,
        CreatedAt = notification.CreatedAt,
        SourceId = notification.SourceId,
        ActionsJson = notification.Actions.Count == 0 ? null : JsonSerializer.Serialize(notification.Actions, JsonOptions),
        ResolutionConditionsJson = notification.ResolutionConditions is null
            ? null
            : JsonSerializer.Serialize(notification.ResolutionConditions, JsonOptions),
        MetadataJson = SerializeMetadata(notification.Metadata),
        ReadAt = notification.ReadAt,
        IsArchived = notification.IsArchived,
        ArchivedAt = notification.ArchivedAt,
        ArchiveReason = notification.ArchiveReason,
    };

    /// <summary>The metadata as stored: null when there is none.</summary>
    public static string? SerializeMetadata(Dictionary<string, object>? metadata) =>
        metadata is null || metadata.Count == 0 ? null : JsonSerializer.Serialize(metadata, JsonOptions);
}
