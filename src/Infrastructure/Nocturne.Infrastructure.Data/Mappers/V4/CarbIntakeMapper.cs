using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between CarbIntake domain models and CarbIntakeEntity database entities
/// </summary>
public static class CarbIntakeMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of CarbIntakeEntity.</returns>
    public static CarbIntakeEntity ToEntity(CarbIntake model)
    {
        return new CarbIntakeEntity
        {
            Carbs = model.Carbs,
            SyncIdentifier = model.SyncIdentifier,
            CarbTime = model.CarbTime,
            AbsorptionTime = model.AbsorptionTime,
            FatGrams = model.FatGrams,
            ProteinGrams = model.ProteinGrams,
        }.WithHeaderFrom(model);
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of CarbIntake domain model.</returns>
    public static CarbIntake ToDomainModel(CarbIntakeEntity entity)
    {
        return new CarbIntake
        {
            Carbs = entity.Carbs,
            SyncIdentifier = entity.SyncIdentifier,
            CarbTime = entity.CarbTime,
            AbsorptionTime = entity.AbsorptionTime,
            FatGrams = entity.FatGrams,
            ProteinGrams = entity.ProteinGrams,
        }.WithHeaderFrom(entity);
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(CarbIntakeEntity entity, CarbIntake model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        entity.Carbs = model.Carbs;
        entity.SyncIdentifier = model.SyncIdentifier;
        entity.CarbTime = model.CarbTime;
        entity.AbsorptionTime = model.AbsorptionTime;
        entity.FatGrams = model.FatGrams;
        entity.ProteinGrams = model.ProteinGrams;
    }
}
