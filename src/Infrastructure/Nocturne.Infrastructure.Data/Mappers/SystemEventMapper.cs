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
            Id = MapperHelpers.ParseIdToGuid(systemEvent.Id),
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
            Metadata = MapperHelpers.DeserializeJson<Dictionary<string, object>>(entity.MetadataJson),
            OriginalId = entity.OriginalId,
            CreatedAt = entity.CreatedAt,
        };
    }
}
