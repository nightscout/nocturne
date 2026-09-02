using System.Text.Json;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between TargetRangeSchedule domain models and TargetRangeScheduleEntity database entities
/// </summary>
public static class TargetRangeScheduleMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of TargetRangeScheduleEntity.</returns>
    public static TargetRangeScheduleEntity ToEntity(TargetRangeSchedule model)
    {
        return new TargetRangeScheduleEntity
        {
            ProfileName = model.ProfileName,
            EntriesJson = JsonSerializer.Serialize(model.Entries),
        }.WithHeaderFrom(model);
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of TargetRangeSchedule domain model.</returns>
    public static TargetRangeSchedule ToDomainModel(TargetRangeScheduleEntity entity)
    {
        return new TargetRangeSchedule
        {
            ProfileName = entity.ProfileName,
            Entries = JsonSerializer.Deserialize<List<TargetRangeEntry>>(entity.EntriesJson) ?? [],
        }.WithHeaderFrom(entity);
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(TargetRangeScheduleEntity entity, TargetRangeSchedule model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        entity.ProfileName = model.ProfileName;
        entity.EntriesJson = JsonSerializer.Serialize(model.Entries);
    }
}
