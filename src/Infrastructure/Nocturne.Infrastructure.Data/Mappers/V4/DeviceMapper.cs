using System.Text.Json;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between Device domain models and DeviceEntity database entities
/// </summary>
public static class DeviceMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of DeviceEntity.</returns>
    public static DeviceEntity ToEntity(Device model)
    {
        return new DeviceEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            Category = model.Category.ToString(),
            Type = model.Type,
            Serial = model.Serial,
            FirstSeenTimestamp = model.FirstSeenTimestamp,
            LastSeenTimestamp = model.LastSeenTimestamp,
            AdditionalPropertiesJson = model.AdditionalProperties is { Count: > 0 }
                ? JsonSerializer.Serialize(model.AdditionalProperties)
                : null,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of Device domain model.</returns>
    public static Device ToDomainModel(DeviceEntity entity)
    {
        return new Device
        {
            Id = entity.Id,
            Category = Enum.TryParse<DeviceCategory>(entity.Category, ignoreCase: true, out var category)
                ? category
                : DeviceCategory.InsulinPump,
            Type = entity.Type,
            Serial = entity.Serial,
            FirstSeenTimestamp = entity.FirstSeenTimestamp,
            LastSeenTimestamp = entity.LastSeenTimestamp,
            AdditionalProperties = MapperHelpers.DeserializeJson<Dictionary<string, object?>>(
                entity.AdditionalPropertiesJson
            ),
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(DeviceEntity entity, Device model)
    {
        entity.Category = model.Category.ToString();
        entity.Type = model.Type;
        entity.Serial = model.Serial;
        entity.FirstSeenTimestamp = model.FirstSeenTimestamp;
        entity.LastSeenTimestamp = model.LastSeenTimestamp;
        entity.AdditionalPropertiesJson = model.AdditionalProperties is { Count: > 0 }
            ? JsonSerializer.Serialize(model.AdditionalProperties)
            : null;
    }
}
