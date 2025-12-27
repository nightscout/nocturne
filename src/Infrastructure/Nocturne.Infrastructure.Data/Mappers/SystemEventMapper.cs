using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between SystemEvent domain models and SystemEventEntity database entities
/// </summary>
public static class SystemEventMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static SystemEventEntity ToEntity(SystemEvent systemEvent)
    {
        return new SystemEventEntity
        {
            Id = string.IsNullOrEmpty(systemEvent.Id)
                ? Guid.CreateVersion7()
                : ParseIdToGuid(systemEvent.Id),
            EventType = systemEvent.EventType.ToString(),
            Category = systemEvent.Category.ToString(),
            Code = systemEvent.Code,
            Description = systemEvent.Description,
            Mills = systemEvent.Mills,
            Source = systemEvent.Source,
            MetadataJson = systemEvent.Metadata != null
                ? JsonSerializer.Serialize(systemEvent.Metadata)
                : null,
            OriginalId = systemEvent.OriginalId,
            CreatedAt = systemEvent.CreatedAt ?? DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static SystemEvent ToDomainModel(SystemEventEntity entity)
    {
        return new SystemEvent
        {
            Id = entity.OriginalId ?? entity.Id.ToString(),
            EventType = Enum.TryParse<SystemEventType>(entity.EventType, out var eventType)
                ? eventType
                : SystemEventType.Info,
            Category = Enum.TryParse<SystemEventCategory>(entity.Category, out var category)
                ? category
                : SystemEventCategory.Pump,
            Code = entity.Code,
            Description = entity.Description,
            Mills = entity.Mills,
            Source = entity.Source,
            Metadata = DeserializeJsonProperty<Dictionary<string, object>>(entity.MetadataJson),
            OriginalId = entity.OriginalId,
            CreatedAt = entity.CreatedAt,
        };
    }

    /// <summary>
    /// Parse string ID to GUID, or generate new GUID if invalid
    /// </summary>
    private static Guid ParseIdToGuid(string id)
    {
        if (string.IsNullOrEmpty(id))
            return Guid.CreateVersion7();

        if (Guid.TryParse(id, out var guidId))
            return guidId;

        // Hash the ID to get a deterministic GUID
        try
        {
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            var hashBytes = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(id));
            var guidBytes = new byte[16];
            Array.Copy(hashBytes, guidBytes, 16);
            return new Guid(guidBytes);
        }
        catch
        {
            return Guid.CreateVersion7();
        }
    }

    /// <summary>
    /// Safely deserialize JSON property
    /// </summary>
    private static T? DeserializeJsonProperty<T>(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null")
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return default;
        }
    }
}
