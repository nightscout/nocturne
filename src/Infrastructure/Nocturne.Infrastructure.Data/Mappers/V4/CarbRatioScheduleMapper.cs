using System.Text.Json;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between CarbRatioSchedule domain models and CarbRatioScheduleEntity database entities
/// </summary>
public static class CarbRatioScheduleMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of CarbRatioScheduleEntity.</returns>
    public static CarbRatioScheduleEntity ToEntity(CarbRatioSchedule model)
    {
        return new CarbRatioScheduleEntity
        {
            ProfileName = model.ProfileName,
            EntriesJson = JsonSerializer.Serialize(model.Entries),
        }.WithHeaderFrom(model);
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of CarbRatioSchedule domain model.</returns>
    public static CarbRatioSchedule ToDomainModel(CarbRatioScheduleEntity entity)
    {
        return new CarbRatioSchedule
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
    public static void UpdateEntity(CarbRatioScheduleEntity entity, CarbRatioSchedule model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        entity.ProfileName = model.ProfileName;
        entity.EntriesJson = JsonSerializer.Serialize(model.Entries);
    }
}
