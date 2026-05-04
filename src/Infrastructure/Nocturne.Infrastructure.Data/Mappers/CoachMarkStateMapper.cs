using Nocturne.Core.Models.CoachMarks;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between CoachMarkState domain models and CoachMarkStateEntity database entities.
/// </summary>
public static class CoachMarkStateMapper
{
    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static CoachMarkState ToDomainModel(CoachMarkStateEntity entity)
    {
        return new CoachMarkState
        {
            Id = entity.Id,
            SubjectId = entity.SubjectId,
            MarkKey = entity.MarkKey,
            Status = entity.Status,
            SeenAt = entity.SeenAt,
            CompletedAt = entity.CompletedAt,
        };
    }

    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static CoachMarkStateEntity ToEntity(CoachMarkState model, Guid tenantId)
    {
        return new CoachMarkStateEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            TenantId = tenantId,
            SubjectId = model.SubjectId,
            MarkKey = model.MarkKey,
            Status = model.Status,
            SeenAt = model.SeenAt,
            CompletedAt = model.CompletedAt,
        };
    }
}
