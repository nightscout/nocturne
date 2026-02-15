using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between Calibration domain models and CalibrationEntity database entities
/// </summary>
public static class CalibrationMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static CalibrationEntity ToEntity(Calibration model)
    {
        return new CalibrationEntity
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
            Slope = model.Slope,
            Intercept = model.Intercept,
            Scale = model.Scale,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static Calibration ToDomainModel(CalibrationEntity entity)
    {
        return new Calibration
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
            Slope = entity.Slope,
            Intercept = entity.Intercept,
            Scale = entity.Scale,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(CalibrationEntity entity, Calibration model)
    {
        entity.Mills = model.Mills;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.App = model.App;
        entity.DataSource = model.DataSource;
        entity.CorrelationId = model.CorrelationId;
        entity.LegacyId = model.LegacyId;
        entity.SysUpdatedAt = DateTime.UtcNow;
        entity.Slope = model.Slope;
        entity.Intercept = model.Intercept;
        entity.Scale = model.Scale;
    }
}
