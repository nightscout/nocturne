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
    public static CarbIntakeEntity ToEntity(CarbIntake model)
    {
        return new CarbIntakeEntity
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
            Carbs = model.Carbs,
            Protein = model.Protein,
            Fat = model.Fat,
            FoodType = model.FoodType,
            AbsorptionTime = model.AbsorptionTime,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static CarbIntake ToDomainModel(CarbIntakeEntity entity)
    {
        return new CarbIntake
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
            Carbs = entity.Carbs,
            Protein = entity.Protein,
            Fat = entity.Fat,
            FoodType = entity.FoodType,
            AbsorptionTime = entity.AbsorptionTime,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(CarbIntakeEntity entity, CarbIntake model)
    {
        entity.Mills = model.Mills;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.App = model.App;
        entity.DataSource = model.DataSource;
        entity.CorrelationId = model.CorrelationId;
        entity.LegacyId = model.LegacyId;
        entity.SysUpdatedAt = DateTime.UtcNow;
        entity.Carbs = model.Carbs;
        entity.Protein = model.Protein;
        entity.Fat = model.Fat;
        entity.FoodType = model.FoodType;
        entity.AbsorptionTime = model.AbsorptionTime;
    }
}
