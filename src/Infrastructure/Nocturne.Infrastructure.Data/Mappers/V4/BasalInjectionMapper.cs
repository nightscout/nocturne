using System.Text.Json;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between BasalInjection domain models and BasalInjectionEntity database entities.
/// Soft-delete state (DeletedAt) is intentionally not round-tripped: it lives below the repository layer.
/// </summary>
public static class BasalInjectionMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of BasalInjectionEntity.</returns>
    public static BasalInjectionEntity ToEntity(BasalInjection model)
    {
        if (model.InsulinContext is null)
            throw new InvalidOperationException(
                $"BasalInjection {model.Id} has null InsulinContext; the InsulinContext property is required.");

        return new BasalInjectionEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            Timestamp = model.Timestamp,
            UtcOffset = model.UtcOffset,
            Device = model.Device,
            App = model.App,
            DataSource = model.DataSource,
            SyncIdentifier = model.SyncIdentifier,
            CorrelationId = model.CorrelationId,
            LegacyId = model.LegacyId,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
            Units = model.Units,
            Notes = model.Notes,
            InsulinContextJson = JsonSerializer.Serialize(model.InsulinContext),
            AdditionalPropertiesJson = model.AdditionalProperties is { Count: > 0 }
                ? JsonSerializer.Serialize(model.AdditionalProperties)
                : null,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of BasalInjection domain model.</returns>
    public static BasalInjection ToDomainModel(BasalInjectionEntity entity)
    {
        var insulinContext = JsonSerializer.Deserialize<TreatmentInsulinContext>(entity.InsulinContextJson)
            ?? throw new InvalidDataException(
                $"BasalInjectionEntity {entity.Id} has invalid InsulinContext JSON: '{entity.InsulinContextJson}'.");

        return new BasalInjection
        {
            Id = entity.Id,
            Timestamp = entity.Timestamp,
            UtcOffset = entity.UtcOffset,
            Device = entity.Device,
            App = entity.App,
            DataSource = entity.DataSource,
            SyncIdentifier = entity.SyncIdentifier,
            CorrelationId = entity.CorrelationId,
            LegacyId = entity.LegacyId,
            CreatedAt = entity.SysCreatedAt,
            ModifiedAt = entity.SysUpdatedAt,
            Units = entity.Units,
            Notes = entity.Notes,
            InsulinContext = insulinContext,
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
    public static void UpdateEntity(BasalInjectionEntity entity, BasalInjection model)
    {
        if (model.InsulinContext is null)
            throw new InvalidOperationException(
                $"BasalInjection {model.Id} has null InsulinContext; the InsulinContext property is required.");

        entity.Timestamp = model.Timestamp;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.App = model.App;
        entity.DataSource = model.DataSource;
        entity.SyncIdentifier = model.SyncIdentifier;
        entity.CorrelationId = model.CorrelationId;
        entity.LegacyId = model.LegacyId;
        entity.SysUpdatedAt = DateTime.UtcNow;
        entity.Units = model.Units;
        entity.Notes = model.Notes;
        entity.InsulinContextJson = JsonSerializer.Serialize(model.InsulinContext);
        entity.AdditionalPropertiesJson = model.AdditionalProperties is { Count: > 0 }
            ? JsonSerializer.Serialize(model.AdditionalProperties)
            : null;
    }
}
