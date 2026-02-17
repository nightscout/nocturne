using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between TreatmentFood domain models and TreatmentFoodEntity database entities.
/// </summary>
public static class TreatmentFoodMapper
{
    /// <summary>
    /// Convert database entity to domain model.
    /// </summary>
    public static TreatmentFood ToDomainModel(TreatmentFoodEntity entity, FoodEntity? food = null)
    {
        return new TreatmentFood
        {
            Id = entity.Id,
            CarbIntakeId = entity.CarbIntakeId,
            FoodId = entity.FoodId,
            Portions = entity.Portions,
            Carbs = entity.Carbs,
            TimeOffsetMinutes = entity.TimeOffsetMinutes,
            Note = entity.Note,
            FoodName = food?.Name,
            CarbsPerPortion = food != null ? (decimal)food.Carbs : null,
        };
    }

    /// <summary>
    /// Convert domain model to database entity.
    /// </summary>
    public static TreatmentFoodEntity ToEntity(TreatmentFood entry)
    {
        return new TreatmentFoodEntity
        {
            Id = entry.Id == Guid.Empty ? Guid.CreateVersion7() : entry.Id,
            CarbIntakeId = entry.CarbIntakeId,
            FoodId = entry.FoodId,
            Portions = entry.Portions,
            Carbs = entry.Carbs,
            TimeOffsetMinutes = entry.TimeOffsetMinutes,
            Note = entry.Note,
        };
    }

    /// <summary>
    /// Update existing entity with values from domain model.
    /// </summary>
    public static void UpdateEntity(TreatmentFoodEntity entity, TreatmentFood entry)
    {
        entity.FoodId = entry.FoodId;
        entity.Portions = entry.Portions;
        entity.Carbs = entry.Carbs;
        entity.TimeOffsetMinutes = entry.TimeOffsetMinutes;
        entity.Note = entry.Note;
    }
}
