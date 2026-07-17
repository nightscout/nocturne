using System.Text.Json;
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
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            Timestamp = model.Timestamp,
            UtcOffset = model.UtcOffset,
            Device = model.Device,
            App = model.App,
            DataSource = model.DataSource,
            CorrelationId = model.CorrelationId,
            LegacyId = model.LegacyId,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
            Carbs = model.Carbs,
            SyncIdentifier = model.SyncIdentifier,
            CarbTime = model.CarbTime,
            AbsorptionTime = model.AbsorptionTime,
            FatGrams = model.FatGrams,
            ProteinGrams = model.ProteinGrams,
            AdditionalPropertiesJson = model.AdditionalProperties is { Count: > 0 }
                ? JsonSerializer.Serialize(model.AdditionalProperties)
                : null,
        };
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
            Id = entity.Id,
            Timestamp = entity.Timestamp,
            UtcOffset = entity.UtcOffset,
            Device = entity.Device,
            App = entity.App,
            DataSource = entity.DataSource,
            CorrelationId = entity.CorrelationId,
            LegacyId = entity.LegacyId,
            CreatedAt = entity.SysCreatedAt,
            ModifiedAt = entity.SysUpdatedAt,
            Carbs = entity.Carbs,
            SyncIdentifier = entity.SyncIdentifier,
            CarbTime = entity.CarbTime,
            AbsorptionTime = entity.AbsorptionTime,
            FatGrams = entity.FatGrams,
            ProteinGrams = entity.ProteinGrams,
            AdditionalProperties = !string.IsNullOrEmpty(entity.AdditionalPropertiesJson)
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.AdditionalPropertiesJson)
                : null,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(CarbIntakeEntity entity, CarbIntake model)
    {
        entity.Timestamp = model.Timestamp;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.App = model.App;
        entity.DataSource = model.DataSource;
        entity.CorrelationId = model.CorrelationId;
        entity.LegacyId = model.LegacyId;
        entity.Carbs = model.Carbs;
        entity.SyncIdentifier = model.SyncIdentifier;
        entity.CarbTime = model.CarbTime;
        entity.AbsorptionTime = model.AbsorptionTime;
        entity.FatGrams = model.FatGrams;
        entity.ProteinGrams = model.ProteinGrams;
        entity.AdditionalPropertiesJson = model.AdditionalProperties is { Count: > 0 }
            ? JsonSerializer.Serialize(model.AdditionalProperties)
            : null;
    }
}
