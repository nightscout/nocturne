using Nocturne.Core.Models.Injectables;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between PenVial domain models and PenVialEntity database entities
/// </summary>
public static class PenVialMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static PenVialEntity ToEntity(PenVial penVial)
    {
        return new PenVialEntity
        {
            Id = penVial.Id == Guid.Empty ? Guid.CreateVersion7() : penVial.Id,
            InjectableMedicationId = penVial.InjectableMedicationId,
            OpenedAt = penVial.OpenedAt,
            ExpiresAt = penVial.ExpiresAt,
            InitialUnits = penVial.InitialUnits,
            RemainingUnits = penVial.RemainingUnits,
            LotNumber = penVial.LotNumber,
            Status = penVial.Status.ToString(),
            Notes = penVial.Notes,
            IsArchived = penVial.IsArchived,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static PenVial ToDomainModel(PenVialEntity entity)
    {
        return new PenVial
        {
            Id = entity.Id,
            InjectableMedicationId = entity.InjectableMedicationId,
            OpenedAt = entity.OpenedAt,
            ExpiresAt = entity.ExpiresAt,
            InitialUnits = entity.InitialUnits,
            RemainingUnits = entity.RemainingUnits,
            LotNumber = entity.LotNumber,
            Status = ParseStatus(entity.Status),
            Notes = entity.Notes,
            IsArchived = entity.IsArchived,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(PenVialEntity entity, PenVial penVial)
    {
        entity.InjectableMedicationId = penVial.InjectableMedicationId;
        entity.OpenedAt = penVial.OpenedAt;
        entity.ExpiresAt = penVial.ExpiresAt;
        entity.InitialUnits = penVial.InitialUnits;
        entity.RemainingUnits = penVial.RemainingUnits;
        entity.LotNumber = penVial.LotNumber;
        entity.Status = penVial.Status.ToString();
        entity.Notes = penVial.Notes;
        entity.IsArchived = penVial.IsArchived;
        entity.SysUpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Parse string to PenVialStatus enum
    /// </summary>
    private static PenVialStatus ParseStatus(string? status)
    {
        if (string.IsNullOrEmpty(status))
            return PenVialStatus.Active;

        return Enum.TryParse<PenVialStatus>(status, ignoreCase: true, out var result)
            ? result
            : PenVialStatus.Active;
    }
}
