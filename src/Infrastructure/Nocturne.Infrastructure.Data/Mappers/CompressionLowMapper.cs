using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Maps between CompressionLowSuggestion domain model and entity
/// </summary>
public static class CompressionLowMapper
{
    /// <summary>
    /// Convert entity to domain model
    /// </summary>
    public static CompressionLowSuggestion ToDomainModel(CompressionLowSuggestionEntity entity)
    {
        return new CompressionLowSuggestion
        {
            Id = entity.Id,
            StartMills = entity.StartMills,
            EndMills = entity.EndMills,
            Confidence = entity.Confidence,
            Status = Enum.Parse<CompressionLowStatus>(entity.Status, ignoreCase: true),
            NightOf = entity.NightOf,
            CreatedAt = entity.CreatedAt,
            ReviewedAt = entity.ReviewedAt,
            StateSpanId = entity.StateSpanId,
            LowestGlucose = entity.LowestGlucose,
            DropRate = entity.DropRate,
            RecoveryMinutes = entity.RecoveryMinutes
        };
    }

    /// <summary>
    /// Convert domain model to new entity
    /// </summary>
    public static CompressionLowSuggestionEntity ToEntity(CompressionLowSuggestion model)
    {
        return new CompressionLowSuggestionEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            StartMills = model.StartMills,
            EndMills = model.EndMills,
            Confidence = model.Confidence,
            Status = model.Status.ToString(),
            NightOf = model.NightOf,
            CreatedAt = model.CreatedAt,
            ReviewedAt = model.ReviewedAt,
            StateSpanId = model.StateSpanId,
            LowestGlucose = model.LowestGlucose,
            DropRate = model.DropRate,
            RecoveryMinutes = model.RecoveryMinutes
        };
    }

    /// <summary>
    /// Update existing entity from domain model
    /// </summary>
    public static void UpdateEntity(CompressionLowSuggestionEntity entity, CompressionLowSuggestion model)
    {
        entity.StartMills = model.StartMills;
        entity.EndMills = model.EndMills;
        entity.Confidence = model.Confidence;
        entity.Status = model.Status.ToString();
        entity.ReviewedAt = model.ReviewedAt;
        entity.StateSpanId = model.StateSpanId;
    }
}
