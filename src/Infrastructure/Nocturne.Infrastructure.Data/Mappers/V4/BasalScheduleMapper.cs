using System.Text.Json;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between BasalSchedule domain models and BasalScheduleEntity database entities
/// </summary>
public static class BasalScheduleMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of BasalScheduleEntity.</returns>
    public static BasalScheduleEntity ToEntity(BasalSchedule model)
    {
        return new BasalScheduleEntity
        {
            ProfileName = model.ProfileName,
            EntriesJson = JsonSerializer.Serialize(model.Entries),
        }.WithHeaderFrom(model);
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of BasalSchedule domain model.</returns>
    public static BasalSchedule ToDomainModel(BasalScheduleEntity entity)
    {
        return new BasalSchedule
        {
            ProfileName = entity.ProfileName,
            Entries = JsonSerializer.Deserialize<List<ScheduleEntry>>(entity.EntriesJson) ?? [],
        }.WithHeaderFrom(entity);
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(BasalScheduleEntity entity, BasalSchedule model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        entity.ProfileName = model.ProfileName;
        entity.EntriesJson = JsonSerializer.Serialize(model.Entries);
    }
}
