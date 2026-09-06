using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between PumpSnapshot domain models and PumpSnapshotEntity database entities
/// </summary>
public static class PumpSnapshotMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of PumpSnapshotEntity.</returns>
    public static PumpSnapshotEntity ToEntity(PumpSnapshot model)
    {
        return new PumpSnapshotEntity
        {
            SyncIdentifier = model.SyncIdentifier,
            Manufacturer = model.Manufacturer,
            Model = model.Model,
            Reservoir = model.Reservoir,
            ReservoirDisplay = model.ReservoirDisplay,
            BatteryPercent = model.BatteryPercent,
            BatteryVoltage = model.BatteryVoltage,
            Bolusing = model.Bolusing,
            Suspended = model.Suspended,
            PumpStatus = model.PumpStatus,
            PumpMode = model.PumpMode,
            Clock = model.Clock,
            DeviceId = model.DeviceId,
            PatientDeviceId = model.PatientDeviceId,
            Iob = model.Iob,
            BolusIob = model.BolusIob,
        }.WithHeaderFrom(model);
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of PumpSnapshot domain model.</returns>
    public static PumpSnapshot ToDomainModel(PumpSnapshotEntity entity)
    {
        return new PumpSnapshot
        {
            SyncIdentifier = entity.SyncIdentifier,
            Manufacturer = entity.Manufacturer,
            Model = entity.Model,
            Reservoir = entity.Reservoir,
            ReservoirDisplay = entity.ReservoirDisplay,
            BatteryPercent = entity.BatteryPercent,
            BatteryVoltage = entity.BatteryVoltage,
            Bolusing = entity.Bolusing,
            Suspended = entity.Suspended,
            PumpStatus = entity.PumpStatus,
            PumpMode = entity.PumpMode,
            Clock = entity.Clock,
            DeviceId = entity.DeviceId,
            PatientDeviceId = entity.PatientDeviceId,
            Iob = entity.Iob,
            BolusIob = entity.BolusIob,
        }.WithHeaderFrom(entity);
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(PumpSnapshotEntity entity, PumpSnapshot model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        entity.SyncIdentifier = model.SyncIdentifier;
        entity.Manufacturer = model.Manufacturer;
        entity.Model = model.Model;
        entity.Reservoir = model.Reservoir;
        entity.ReservoirDisplay = model.ReservoirDisplay;
        entity.BatteryPercent = model.BatteryPercent;
        entity.BatteryVoltage = model.BatteryVoltage;
        entity.Bolusing = model.Bolusing;
        entity.Suspended = model.Suspended;
        entity.PumpStatus = model.PumpStatus;
        entity.PumpMode = model.PumpMode;
        entity.Clock = model.Clock;
        entity.DeviceId = model.DeviceId;
        entity.PatientDeviceId = model.PatientDeviceId;
        entity.Iob = model.Iob;
        entity.BolusIob = model.BolusIob;
    }
}
