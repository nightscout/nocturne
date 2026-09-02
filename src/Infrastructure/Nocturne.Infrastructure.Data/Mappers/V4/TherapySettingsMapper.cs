using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between TherapySettings domain models and TherapySettingsEntity database entities
/// </summary>
public static class TherapySettingsMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of TherapySettingsEntity.</returns>
    public static TherapySettingsEntity ToEntity(TherapySettings model)
    {
        return new TherapySettingsEntity
        {
            ProfileName = model.ProfileName,
            Timezone = model.Timezone,
            Units = model.Units,
            Dia = model.Dia,
            CarbsHr = model.CarbsHr,
            Delay = model.Delay,
            PerGiValues = model.PerGIValues,
            CarbsHrHigh = model.CarbsHrHigh,
            CarbsHrMedium = model.CarbsHrMedium,
            CarbsHrLow = model.CarbsHrLow,
            DelayHigh = model.DelayHigh,
            DelayMedium = model.DelayMedium,
            DelayLow = model.DelayLow,
            LoopSettingsJson = model.LoopSettings is not null
                ? JsonSerializer.Serialize(model.LoopSettings)
                : null,
            IsDefault = model.IsDefault,
            EnteredBy = model.EnteredBy,
            IsExternallyManaged = model.IsExternallyManaged,
            StartDate = model.StartDate,
        }.WithHeaderFrom(model);
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of TherapySettings domain model.</returns>
    public static TherapySettings ToDomainModel(TherapySettingsEntity entity)
    {
        return new TherapySettings
        {
            ProfileName = entity.ProfileName,
            Timezone = entity.Timezone,
            Units = entity.Units,
            Dia = entity.Dia,
            CarbsHr = entity.CarbsHr,
            Delay = entity.Delay,
            PerGIValues = entity.PerGiValues,
            CarbsHrHigh = entity.CarbsHrHigh,
            CarbsHrMedium = entity.CarbsHrMedium,
            CarbsHrLow = entity.CarbsHrLow,
            DelayHigh = entity.DelayHigh,
            DelayMedium = entity.DelayMedium,
            DelayLow = entity.DelayLow,
            LoopSettings = !string.IsNullOrEmpty(entity.LoopSettingsJson)
                ? JsonSerializer.Deserialize<LoopProfileSettings>(entity.LoopSettingsJson)
                : null,
            IsDefault = entity.IsDefault,
            EnteredBy = entity.EnteredBy,
            IsExternallyManaged = entity.IsExternallyManaged,
            StartDate = entity.StartDate,
        }.WithHeaderFrom(entity);
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(TherapySettingsEntity entity, TherapySettings model)
    {
        V4RecordHeaderMapper.UpdateHeader(entity, model);
        entity.ProfileName = model.ProfileName;
        entity.Timezone = model.Timezone;
        entity.Units = model.Units;
        entity.Dia = model.Dia;
        entity.CarbsHr = model.CarbsHr;
        entity.Delay = model.Delay;
        entity.PerGiValues = model.PerGIValues;
        entity.CarbsHrHigh = model.CarbsHrHigh;
        entity.CarbsHrMedium = model.CarbsHrMedium;
        entity.CarbsHrLow = model.CarbsHrLow;
        entity.DelayHigh = model.DelayHigh;
        entity.DelayMedium = model.DelayMedium;
        entity.DelayLow = model.DelayLow;
        entity.LoopSettingsJson = model.LoopSettings is not null
            ? JsonSerializer.Serialize(model.LoopSettings)
            : null;
        entity.IsDefault = model.IsDefault;
        entity.EnteredBy = model.EnteredBy;
        entity.IsExternallyManaged = model.IsExternallyManaged;
        entity.StartDate = model.StartDate;
    }
}
