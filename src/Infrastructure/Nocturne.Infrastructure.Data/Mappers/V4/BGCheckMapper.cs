using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between BGCheck domain models and BGCheckEntity database entities
/// </summary>
public static class BGCheckMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static BGCheckEntity ToEntity(BGCheck model)
    {
        return new BGCheckEntity
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
            Glucose = model.Glucose,
            GlucoseType = model.GlucoseType?.ToString(),
            Mgdl = model.Mgdl,
            Mmol = model.Mmol,
            Units = model.Units?.ToString(),
            SyncIdentifier = model.SyncIdentifier,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static BGCheck ToDomainModel(BGCheckEntity entity)
    {
        return new BGCheck
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
            Glucose = entity.Glucose,
            GlucoseType = Enum.TryParse<GlucoseType>(entity.GlucoseType, out var gt) ? gt : null,
            Mgdl = entity.Mgdl,
            Mmol = entity.Mmol,
            Units = Enum.TryParse<GlucoseUnit>(entity.Units, out var u) ? u : null,
            SyncIdentifier = entity.SyncIdentifier,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(BGCheckEntity entity, BGCheck model)
    {
        entity.Mills = model.Mills;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.App = model.App;
        entity.DataSource = model.DataSource;
        entity.CorrelationId = model.CorrelationId;
        entity.LegacyId = model.LegacyId;
        entity.SysUpdatedAt = DateTime.UtcNow;
        entity.Glucose = model.Glucose;
        entity.GlucoseType = model.GlucoseType?.ToString();
        entity.Mgdl = model.Mgdl;
        entity.Mmol = model.Mmol;
        entity.Units = model.Units?.ToString();
        entity.SyncIdentifier = model.SyncIdentifier;
    }
}
