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
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of BGCheckEntity.</returns>
    public static BGCheckEntity ToEntity(BGCheck model)
    {
        return new BGCheckEntity
        {
            Glucose = model.Glucose,
            GlucoseType = model.GlucoseType?.ToString(),
            Units = model.Units?.ToString(),
            SyncIdentifier = model.SyncIdentifier,
        }.WithHeaderFrom(model);
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of BGCheck domain model.</returns>
    public static BGCheck ToDomainModel(BGCheckEntity entity)
    {
        return new BGCheck
        {
            Glucose = entity.Glucose,
            GlucoseType = Enum.TryParse<GlucoseType>(entity.GlucoseType, out var gt) ? gt : null,
            Units = Enum.TryParse<GlucoseUnit>(entity.Units, out var u) ? u : null,
            SyncIdentifier = entity.SyncIdentifier,
        }.WithHeaderFrom(entity);
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(BGCheckEntity entity, BGCheck model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        entity.Glucose = model.Glucose;
        entity.GlucoseType = model.GlucoseType?.ToString();
        entity.Units = model.Units?.ToString();
        entity.SyncIdentifier = model.SyncIdentifier;
    }
}
