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
    public static UploaderSnapshotEntity ToEntity(UploaderSnapshot model)
    {
        return new UploaderSnapshotEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            Mills = model.Mills,
            UtcOffset = model.UtcOffset,
            Device = model.Device,
            LegacyId = model.LegacyId,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
            Name = model.Name,
            Battery = model.Battery,
            BatteryVoltage = model.BatteryVoltage,
            IsCharging = model.IsCharging,
            Temperature = model.Temperature,
            Type = model.Type,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static UploaderSnapshot ToDomainModel(UploaderSnapshotEntity entity)
    {
        return new UploaderSnapshot
        {
            Id = entity.Id,
            Mills = entity.Mills,
            UtcOffset = entity.UtcOffset,
            Device = entity.Device,
            LegacyId = entity.LegacyId,
            CreatedAt = entity.SysCreatedAt,
            ModifiedAt = entity.SysUpdatedAt,
            Name = entity.Name,
            Battery = entity.Battery,
            BatteryVoltage = entity.BatteryVoltage,
            IsCharging = entity.IsCharging,
            Temperature = entity.Temperature,
            Type = entity.Type,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(UploaderSnapshotEntity entity, UploaderSnapshot model)
    {
        entity.Mills = model.Mills;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.LegacyId = model.LegacyId;
        entity.SysUpdatedAt = DateTime.UtcNow;
        entity.Name = model.Name;
        entity.Battery = model.Battery;
        entity.BatteryVoltage = model.BatteryVoltage;
        entity.IsCharging = model.IsCharging;
        entity.Temperature = model.Temperature;
        entity.Type = model.Type;
    }
}
