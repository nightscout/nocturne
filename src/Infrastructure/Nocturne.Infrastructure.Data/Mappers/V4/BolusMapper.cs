using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between Bolus domain models and BolusEntity database entities
/// </summary>
public static class BolusMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static BolusEntity ToEntity(Bolus model)
    {
        return new BolusEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            Mills = model.Mills,
            UtcOffset = model.UtcOffset,
            Device = model.Device,
            App = model.App,
            DataSource = model.DataSource,
            CorrelationId = model.CorrelationId,
            LegacyId = model.LegacyId,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
            Insulin = model.Insulin,
            Programmed = model.Programmed,
            Delivered = model.Delivered,
            BolusType = model.BolusType?.ToString(),
            Automatic = model.Automatic,
            Duration = model.Duration,
            SyncIdentifier = model.SyncIdentifier,
            InsulinType = model.InsulinType,
            Unabsorbed = model.Unabsorbed,
            IsBasalInsulin = model.IsBasalInsulin,
            PumpId = model.PumpId,
            PumpSerial = model.PumpSerial,
            PumpType = model.PumpType,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static Bolus ToDomainModel(BolusEntity entity)
    {
        return new Bolus
        {
            Id = entity.Id,
            Mills = entity.Mills,
            UtcOffset = entity.UtcOffset,
            Device = entity.Device,
            App = entity.App,
            DataSource = entity.DataSource,
            CorrelationId = entity.CorrelationId,
            LegacyId = entity.LegacyId,
            CreatedAt = entity.SysCreatedAt,
            ModifiedAt = entity.SysUpdatedAt,
            Insulin = entity.Insulin,
            Programmed = entity.Programmed,
            Delivered = entity.Delivered,
            BolusType = Enum.TryParse<BolusType>(entity.BolusType, out var bt) ? bt : null,
            Automatic = entity.Automatic,
            Duration = entity.Duration,
            SyncIdentifier = entity.SyncIdentifier,
            InsulinType = entity.InsulinType,
            Unabsorbed = entity.Unabsorbed,
            IsBasalInsulin = entity.IsBasalInsulin,
            PumpId = entity.PumpId,
            PumpSerial = entity.PumpSerial,
            PumpType = entity.PumpType,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(BolusEntity entity, Bolus model)
    {
        entity.Mills = model.Mills;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.App = model.App;
        entity.DataSource = model.DataSource;
        entity.CorrelationId = model.CorrelationId;
        entity.LegacyId = model.LegacyId;
        entity.SysUpdatedAt = DateTime.UtcNow;
        entity.Insulin = model.Insulin;
        entity.Programmed = model.Programmed;
        entity.Delivered = model.Delivered;
        entity.BolusType = model.BolusType?.ToString();
        entity.Automatic = model.Automatic;
        entity.Duration = model.Duration;
        entity.SyncIdentifier = model.SyncIdentifier;
        entity.InsulinType = model.InsulinType;
        entity.Unabsorbed = model.Unabsorbed;
        entity.IsBasalInsulin = model.IsBasalInsulin;
        entity.PumpId = model.PumpId;
        entity.PumpSerial = model.PumpSerial;
        entity.PumpType = model.PumpType;
    }
}
