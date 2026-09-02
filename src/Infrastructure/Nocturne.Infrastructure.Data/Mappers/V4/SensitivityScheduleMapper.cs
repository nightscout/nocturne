using System.Text.Json;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between SensitivitySchedule domain models and SensitivityScheduleEntity database entities
/// </summary>
public static class SensitivityScheduleMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of SensitivityScheduleEntity.</returns>
    public static SensitivityScheduleEntity ToEntity(SensitivitySchedule model)
    {
        return new SensitivityScheduleEntity
        {
            ProfileName = model.ProfileName,
            EntriesJson = JsonSerializer.Serialize(model.Entries),
        }.WithHeaderFrom(model);
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of SensitivitySchedule domain model.</returns>
    public static SensitivitySchedule ToDomainModel(SensitivityScheduleEntity entity)
    {
        return new SensitivitySchedule
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
    public static void UpdateEntity(SensitivityScheduleEntity entity, SensitivitySchedule model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        entity.ProfileName = model.ProfileName;
        entity.EntriesJson = JsonSerializer.Serialize(model.Entries);
    }
}
