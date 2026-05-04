using System.Text.Json;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between UploaderSnapshot domain models and UploaderSnapshotEntity database entities
/// </summary>
public static class UploaderSnapshotMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of UploaderSnapshotEntity.</returns>
    public static UploaderSnapshotEntity ToEntity(UploaderSnapshot model)
    {
        return new UploaderSnapshotEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            Timestamp = model.Timestamp,
            UtcOffset = model.UtcOffset,
            Device = model.Device,
            CorrelationId = model.CorrelationId,
            LegacyId = model.LegacyId,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
            Name = model.Name,
            Battery = model.Battery,
            BatteryVoltage = model.BatteryVoltage,
            IsCharging = model.IsCharging,
            Temperature = model.Temperature,
            Type = model.Type,
            DeviceId = model.DeviceId,
            AdditionalPropertiesJson = model.AdditionalProperties is { Count: > 0 }
                ? JsonSerializer.Serialize(model.AdditionalProperties)
                : null,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of UploaderSnapshot domain model.</returns>
    public static UploaderSnapshot ToDomainModel(UploaderSnapshotEntity entity)
    {
        return new UploaderSnapshot
        {
            Id = entity.Id,
            Timestamp = entity.Timestamp,
            UtcOffset = entity.UtcOffset,
            Device = entity.Device,
            CorrelationId = entity.CorrelationId,
            LegacyId = entity.LegacyId,
            CreatedAt = entity.SysCreatedAt,
            ModifiedAt = entity.SysUpdatedAt,
            Name = entity.Name,
            Battery = entity.Battery,
            BatteryVoltage = entity.BatteryVoltage,
            IsCharging = entity.IsCharging,
            Temperature = entity.Temperature,
            Type = entity.Type,
            DeviceId = entity.DeviceId,
            AdditionalProperties = !string.IsNullOrEmpty(entity.AdditionalPropertiesJson)
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.AdditionalPropertiesJson)
                : null,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(UploaderSnapshotEntity entity, UploaderSnapshot model)
    {
        entity.Timestamp = model.Timestamp;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.CorrelationId = model.CorrelationId;
        entity.LegacyId = model.LegacyId;
        entity.SysUpdatedAt = DateTime.UtcNow;
        entity.Name = model.Name;
        entity.Battery = model.Battery;
        entity.BatteryVoltage = model.BatteryVoltage;
        entity.IsCharging = model.IsCharging;
        entity.Temperature = model.Temperature;
        entity.Type = model.Type;
        entity.DeviceId = model.DeviceId;
        entity.AdditionalPropertiesJson = model.AdditionalProperties is { Count: > 0 }
            ? JsonSerializer.Serialize(model.AdditionalProperties)
            : null;
    }
}
