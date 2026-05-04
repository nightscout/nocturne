using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Maps between <see cref="DecompositionBatch"/> domain models and <see cref="DecompositionBatchEntity"/> database entities.
/// </summary>
public static class DecompositionBatchMapper
{
    /// <summary>
    /// Converts a database entity to its domain model representation.
    /// </summary>
    public static DecompositionBatch ToModel(DecompositionBatchEntity entity)
    {
        return new DecompositionBatch
        {
            Id = entity.Id,
            Source = entity.Source,
            SourceRecordId = entity.SourceRecordId,

            CreatedAt = entity.CreatedAt,
        };
    }

    /// <summary>
    /// Converts a domain model to a tenant-scoped database entity.
    /// </summary>
    public static DecompositionBatchEntity ToEntity(DecompositionBatch model, Guid tenantId)
    {
        return new DecompositionBatchEntity
        {
            Id = model.Id,
            TenantId = tenantId,
            Source = model.Source,
            SourceRecordId = model.SourceRecordId,
            CreatedAt = model.CreatedAt,
        };
    }
}
