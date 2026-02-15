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
    public static PumpSnapshotEntity ToEntity(PumpSnapshot model)
    {
        return new PumpSnapshotEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            Mills = model.Mills,
            UtcOffset = model.UtcOffset,
            Device = model.Device,
            LegacyId = model.LegacyId,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
            Manufacturer = model.Manufacturer,
            Model = model.Model,
            Reservoir = model.Reservoir,
            ReservoirDisplay = model.ReservoirDisplay,
            BatteryPercent = model.BatteryPercent,
            BatteryVoltage = model.BatteryVoltage,
            Bolusing = model.Bolusing,
            Suspended = model.Suspended,
            PumpStatus = model.PumpStatus,
            Clock = model.Clock,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static PumpSnapshot ToDomainModel(PumpSnapshotEntity entity)
    {
        return new PumpSnapshot
        {
            Id = entity.Id,
            Mills = entity.Mills,
            UtcOffset = entity.UtcOffset,
            Device = entity.Device,
            LegacyId = entity.LegacyId,
            CreatedAt = entity.SysCreatedAt,
            ModifiedAt = entity.SysUpdatedAt,
            Manufacturer = entity.Manufacturer,
            Model = entity.Model,
            Reservoir = entity.Reservoir,
            ReservoirDisplay = entity.ReservoirDisplay,
            BatteryPercent = entity.BatteryPercent,
            BatteryVoltage = entity.BatteryVoltage,
            Bolusing = entity.Bolusing,
            Suspended = entity.Suspended,
            PumpStatus = entity.PumpStatus,
            Clock = entity.Clock,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(PumpSnapshotEntity entity, PumpSnapshot model)
    {
        entity.Mills = model.Mills;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.LegacyId = model.LegacyId;
        entity.SysUpdatedAt = DateTime.UtcNow;
        entity.Manufacturer = model.Manufacturer;
        entity.Model = model.Model;
        entity.Reservoir = model.Reservoir;
        entity.ReservoirDisplay = model.ReservoirDisplay;
        entity.BatteryPercent = model.BatteryPercent;
        entity.BatteryVoltage = model.BatteryVoltage;
        entity.Bolusing = model.Bolusing;
        entity.Suspended = model.Suspended;
        entity.PumpStatus = model.PumpStatus;
        entity.Clock = model.Clock;
    }
}
