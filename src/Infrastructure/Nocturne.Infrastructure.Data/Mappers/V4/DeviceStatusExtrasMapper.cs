using System.Text.Json;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between DeviceStatusExtras domain models and DeviceStatusExtrasEntity database entities
/// </summary>
public static class DeviceStatusExtrasMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of DeviceStatusExtrasEntity.</returns>
    public static DeviceStatusExtrasEntity ToEntity(DeviceStatusExtras model)
    {
        return new DeviceStatusExtrasEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            TenantId = model.TenantId,
            CorrelationId = model.CorrelationId,
            Timestamp = model.Timestamp,
            ExtrasJson = model.Extras is { Count: > 0 }
                ? JsonSerializer.Serialize(model.Extras)
                : null,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of DeviceStatusExtras domain model.</returns>
    public static DeviceStatusExtras ToDomainModel(DeviceStatusExtrasEntity entity)
    {
        return new DeviceStatusExtras
        {
            Id = entity.Id,
            TenantId = entity.TenantId,
            CorrelationId = entity.CorrelationId,
            Timestamp = entity.Timestamp,
            Extras = !string.IsNullOrEmpty(entity.ExtrasJson)
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.ExtrasJson)
                : null,
            CreatedAt = entity.SysCreatedAt,
            ModifiedAt = entity.SysUpdatedAt,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(DeviceStatusExtrasEntity entity, DeviceStatusExtras model)
    {
        entity.CorrelationId = model.CorrelationId;
        entity.Timestamp = model.Timestamp;
        entity.ExtrasJson = model.Extras is { Count: > 0 }
            ? JsonSerializer.Serialize(model.Extras)
            : null;
        entity.SysUpdatedAt = DateTime.UtcNow;
    }
}
