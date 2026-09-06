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
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of MeterGlucoseEntity.</returns>
    public static MeterGlucoseEntity ToEntity(MeterGlucose model)
    {
        return new MeterGlucoseEntity
        {
            PatientDeviceId = model.PatientDeviceId,
            Mgdl = model.Mgdl,
        }.WithHeaderFrom(model);
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of MeterGlucose domain model.</returns>
    public static MeterGlucose ToDomainModel(MeterGlucoseEntity entity)
    {
        return new MeterGlucose
        {
            PatientDeviceId = entity.PatientDeviceId,
            Mgdl = entity.Mgdl,
        }.WithHeaderFrom(entity);
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(MeterGlucoseEntity entity, MeterGlucose model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        entity.PatientDeviceId = model.PatientDeviceId;
        entity.Mgdl = model.Mgdl;
    }
}
