using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Common;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between Food domain models and FoodEntity database entities
/// </summary>
public static class FoodMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static FoodEntity ToEntity(Food food)
    {
        return new FoodEntity
        {
            Id = string.IsNullOrEmpty(food.Id) ? Guid.CreateVersion7() : ParseIdToGuid(food.Id),
            OriginalId = MongoIdUtils.IsValidMongoId(food.Id) ? food.Id : null,
            Type = food.Type,
            Category = food.Category,
            Subcategory = food.Subcategory,
            Name = food.Name,
            Portion = food.Portion,
            Carbs = food.Carbs,
            Fat = food.Fat,
            Protein = food.Protein,
            Energy = food.Energy,
            Gi = (GlycemicIndex)food.Gi,
            Unit = food.Unit,
            Foods = food.Foods != null ? JsonSerializer.Serialize(food.Foods) : null,
            HideAfterUse = food.HideAfterUse,
            Hidden = food.Hidden,
            Position = food.Position,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static Food ToDomainModel(FoodEntity entity)
    {
        return new Food
        {
            Id = entity.OriginalId ?? entity.Id.ToString(),
            Type = entity.Type,
            Category = entity.Category,
            Subcategory = entity.Subcategory,
            Name = entity.Name,
            Portion = entity.Portion,
            Carbs = entity.Carbs,
            Fat = entity.Fat,
            Protein = entity.Protein,
            Energy = entity.Energy,
            Gi = (int)entity.Gi,
            Unit = entity.Unit,
            Foods = !string.IsNullOrEmpty(entity.Foods)
                ? JsonSerializer.Deserialize<List<QuickPickFood>>(entity.Foods)
                : null,
            HideAfterUse = entity.HideAfterUse,
            Hidden = entity.Hidden,
            Position = entity.Position,
            // Map SysCreatedAt to created_at for Nightscout parity
            CreatedAt = entity.SysCreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };
    }

    /// <summary>
    /// Update entity with values from domain model
    /// </summary>
    public static void UpdateEntity(FoodEntity entity, Food food)
    {
        entity.Type = food.Type;
        entity.Category = food.Category;
        entity.Subcategory = food.Subcategory;
        entity.Name = food.Name;
        entity.Portion = food.Portion;
        entity.Carbs = food.Carbs;
        entity.Fat = food.Fat;
        entity.Protein = food.Protein;
        entity.Energy = food.Energy;
        entity.Gi = (GlycemicIndex)food.Gi;
        entity.Unit = food.Unit;
        entity.Foods = food.Foods != null ? JsonSerializer.Serialize(food.Foods) : null;
        entity.HideAfterUse = food.HideAfterUse;
        entity.Hidden = food.Hidden;
        entity.Position = food.Position;
        entity.SysUpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Convert string ID to GUID for consistent mapping
    /// </summary>
    private static Guid ParseIdToGuid(string id)
    {
        if (Guid.TryParse(id, out var guid))
        {
            return guid;
        }

        // For string IDs, create a deterministic GUID
        // This ensures consistent mapping between string ID and GUID
        var bytes = System.Text.Encoding.UTF8.GetBytes(id.PadRight(16, '0')[..16]);
        return new Guid(bytes);
    }
}
