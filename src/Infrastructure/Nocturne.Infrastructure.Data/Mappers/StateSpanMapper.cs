using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between StateSpan domain models and StateSpanEntity database entities
/// </summary>
public static class StateSpanMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static StateSpanEntity ToEntity(StateSpan stateSpan)
    {
        return new StateSpanEntity
        {
            Id = MapperHelpers.ParseIdToGuid(stateSpan.Id),
            Category = stateSpan.Category.ToString(),
            State = stateSpan.State ?? string.Empty,
            StartTimestamp = stateSpan.StartTimestamp,
            EndTimestamp = stateSpan.EndTimestamp,
            Source = stateSpan.Source,
            MetadataJson = stateSpan.Metadata != null
                ? JsonSerializer.Serialize(stateSpan.Metadata)
                : null,
            OriginalId = stateSpan.OriginalId,
            SupersededById = !string.IsNullOrEmpty(stateSpan.SupersededById)
                ? MapperHelpers.ParseIdToGuid(stateSpan.SupersededById)
                : null,
            CreatedAt = stateSpan.CreatedAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static StateSpan ToDomainModel(StateSpanEntity entity)
    {
        return new StateSpan
        {
            Id = entity.OriginalId ?? entity.Id.ToString(),
            Category = Enum.TryParse<StateSpanCategory>(entity.Category, out var category)
                ? category
                : StateSpanCategory.PumpMode,
            State = entity.State,
            StartTimestamp = entity.StartTimestamp,
            EndTimestamp = entity.EndTimestamp,
            Source = entity.Source,
            Metadata = MapperHelpers.DeserializeJson<Dictionary<string, object>>(entity.MetadataJson),
            OriginalId = entity.OriginalId,
            SupersededById = entity.SupersededById?.ToString(),
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(StateSpanEntity entity, StateSpan stateSpan)
    {
        entity.Category = stateSpan.Category.ToString();
        entity.State = stateSpan.State ?? string.Empty;
        entity.StartTimestamp = stateSpan.StartTimestamp;
        entity.EndTimestamp = stateSpan.EndTimestamp;
        entity.Source = stateSpan.Source;
        entity.MetadataJson = stateSpan.Metadata != null
            ? JsonSerializer.Serialize(stateSpan.Metadata)
            : null;
        entity.OriginalId = stateSpan.OriginalId;
    }
}
