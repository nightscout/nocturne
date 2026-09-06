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
            SyncIdentifier = model.SyncIdentifier,
            Name = model.Name,
            Battery = model.Battery,
            BatteryVoltage = model.BatteryVoltage,
            IsCharging = model.IsCharging,
            Temperature = model.Temperature,
            Type = model.Type,
            DeviceId = model.DeviceId,
        }.WithHeaderFrom(model);
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
            SyncIdentifier = entity.SyncIdentifier,
            Name = entity.Name,
            Battery = entity.Battery,
            BatteryVoltage = entity.BatteryVoltage,
            IsCharging = entity.IsCharging,
            Temperature = entity.Temperature,
            Type = entity.Type,
            DeviceId = entity.DeviceId,
        }.WithHeaderFrom(entity);
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(UploaderSnapshotEntity entity, UploaderSnapshot model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        entity.SyncIdentifier = model.SyncIdentifier;
        entity.Name = model.Name;
        entity.Battery = model.Battery;
        entity.BatteryVoltage = model.BatteryVoltage;
        entity.IsCharging = model.IsCharging;
        entity.Temperature = model.Temperature;
        entity.Type = model.Type;
        entity.DeviceId = model.DeviceId;
    }
}
