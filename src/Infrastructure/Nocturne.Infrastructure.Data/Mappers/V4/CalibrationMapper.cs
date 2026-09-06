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
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of CalibrationEntity.</returns>
    public static CalibrationEntity ToEntity(Calibration model)
    {
        return new CalibrationEntity
        {
            Slope = model.Slope,
            Intercept = model.Intercept,
            Scale = model.Scale,
        }.WithHeaderFrom(model);
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of Calibration domain model.</returns>
    public static Calibration ToDomainModel(CalibrationEntity entity)
    {
        return new Calibration
        {
            Slope = entity.Slope,
            Intercept = entity.Intercept,
            Scale = entity.Scale,
        }.WithHeaderFrom(entity);
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(CalibrationEntity entity, Calibration model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        entity.Slope = model.Slope;
        entity.Intercept = model.Intercept;
        entity.Scale = model.Scale;
    }
}
