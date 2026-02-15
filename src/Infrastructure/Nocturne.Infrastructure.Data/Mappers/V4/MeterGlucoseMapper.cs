using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between MeterGlucose domain models and MeterGlucoseEntity database entities
/// </summary>
public static class MeterGlucoseMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static MeterGlucoseEntity ToEntity(MeterGlucose model)
    {
        return new MeterGlucoseEntity
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
            Mgdl = model.Mgdl,
            Mmol = model.Mmol,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static MeterGlucose ToDomainModel(MeterGlucoseEntity entity)
    {
        return new MeterGlucose
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
            Mgdl = entity.Mgdl,
            Mmol = entity.Mmol,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(MeterGlucoseEntity entity, MeterGlucose model)
    {
        entity.Mills = model.Mills;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.App = model.App;
        entity.DataSource = model.DataSource;
        entity.CorrelationId = model.CorrelationId;
        entity.LegacyId = model.LegacyId;
        entity.SysUpdatedAt = DateTime.UtcNow;
        entity.Mgdl = model.Mgdl;
        entity.Mmol = model.Mmol;
    }
}
