using Nocturne.Core.Models.Injectables;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between InjectableDose domain models and InjectableDoseEntity database entities
/// </summary>
public static class InjectableDoseMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static InjectableDoseEntity ToEntity(InjectableDose dose)
    {
        return new InjectableDoseEntity
        {
            Id = dose.Id == Guid.Empty ? Guid.CreateVersion7() : dose.Id,
            InjectableMedicationId = dose.InjectableMedicationId,
            Units = dose.Units,
            Timestamp = dose.Timestamp,
            InjectionSite = dose.InjectionSite?.ToString(),
            PenVialId = dose.PenVialId,
            LotNumber = dose.LotNumber,
            Notes = dose.Notes,
            EnteredBy = dose.EnteredBy,
            Source = dose.Source,
            OriginalId = dose.OriginalId,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static InjectableDose ToDomainModel(InjectableDoseEntity entity)
    {
        return new InjectableDose
        {
            Id = entity.Id,
            InjectableMedicationId = entity.InjectableMedicationId,
            Units = entity.Units,
            Timestamp = entity.Timestamp,
            InjectionSite = ParseInjectionSite(entity.InjectionSite),
            PenVialId = entity.PenVialId,
            LotNumber = entity.LotNumber,
            Notes = entity.Notes,
            EnteredBy = entity.EnteredBy,
            Source = entity.Source,
            OriginalId = entity.OriginalId,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(InjectableDoseEntity entity, InjectableDose dose)
    {
        entity.InjectableMedicationId = dose.InjectableMedicationId;
        entity.Units = dose.Units;
        entity.Timestamp = dose.Timestamp;
        entity.InjectionSite = dose.InjectionSite?.ToString();
        entity.PenVialId = dose.PenVialId;
        entity.LotNumber = dose.LotNumber;
        entity.Notes = dose.Notes;
        entity.EnteredBy = dose.EnteredBy;
        entity.Source = dose.Source;
        entity.OriginalId = dose.OriginalId;
        entity.SysUpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Parse string to InjectionSite enum (nullable)
    /// </summary>
    private static InjectionSite? ParseInjectionSite(string? injectionSite)
    {
        if (string.IsNullOrEmpty(injectionSite))
            return null;

        return Enum.TryParse<InjectionSite>(injectionSite, ignoreCase: true, out var result)
            ? result
            : null;
    }
}
