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
        return new BasalInjectionEntity
        {
            SyncIdentifier = model.SyncIdentifier,
            PatientDeviceId = model.PatientDeviceId,
            Units = model.Units,
            Notes = model.Notes,
            InsulinContextJson = model.InsulinContext is not null
                ? JsonSerializer.Serialize(model.InsulinContext)
                : null,
        }.WithHeaderFrom(model);
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of BasalInjection domain model.</returns>
    public static BasalInjection ToDomainModel(BasalInjectionEntity entity)
    {
        return new BasalInjection
        {
            SyncIdentifier = entity.SyncIdentifier,
            PatientDeviceId = entity.PatientDeviceId,
            Units = entity.Units,
            Notes = entity.Notes,
            InsulinContext = !string.IsNullOrEmpty(entity.InsulinContextJson)
                ? JsonSerializer.Deserialize<TreatmentInsulinContext>(entity.InsulinContextJson)
                : null,
        }.WithHeaderFrom(entity);
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(BasalInjectionEntity entity, BasalInjection model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        entity.SyncIdentifier = model.SyncIdentifier;
        entity.PatientDeviceId = model.PatientDeviceId;
        entity.Units = model.Units;
        entity.Notes = model.Notes;
        entity.InsulinContextJson = model.InsulinContext is not null
            ? JsonSerializer.Serialize(model.InsulinContext)
            : null;
    }
}
