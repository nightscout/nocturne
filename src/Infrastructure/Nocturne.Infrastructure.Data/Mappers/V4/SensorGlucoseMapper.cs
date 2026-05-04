using System.Text.Json;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between SensorGlucose domain models and SensorGlucoseEntity database entities
/// </summary>
public static class SensorGlucoseMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of SensorGlucoseEntity.</returns>
    public static SensorGlucoseEntity ToEntity(SensorGlucose model)
    {
        return new SensorGlucoseEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            Timestamp = model.Timestamp,
            UtcOffset = model.UtcOffset,
            Device = model.Device,
            App = model.App,
            DataSource = model.DataSource,
            CorrelationId = model.CorrelationId,
            PatientDeviceId = model.PatientDeviceId,
            LegacyId = model.LegacyId,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
            Mgdl = model.Mgdl,
            Direction = model.Direction?.ToString(),
            TrendRate = model.TrendRate,
            Noise = model.Noise,
            Filtered = model.Filtered,
            Unfiltered = model.Unfiltered,
            Delta = model.Delta,
            GlucoseProcessing = model.GlucoseProcessing?.ToString(),
            SmoothedMgdl = model.SmoothedMgdl,
            UnsmoothedMgdl = model.UnsmoothedMgdl,
            AdditionalPropertiesJson = model.AdditionalProperties is { Count: > 0 }
                ? JsonSerializer.Serialize(model.AdditionalProperties)
                : null,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of SensorGlucose domain model.</returns>
    public static SensorGlucose ToDomainModel(SensorGlucoseEntity entity)
    {
        return new SensorGlucose
        {
            Id = entity.Id,
            Timestamp = entity.Timestamp,
            UtcOffset = entity.UtcOffset,
            Device = entity.Device,
            App = entity.App,
            DataSource = entity.DataSource,
            CorrelationId = entity.CorrelationId,
            PatientDeviceId = entity.PatientDeviceId,
            LegacyId = entity.LegacyId,
            CreatedAt = entity.SysCreatedAt,
            ModifiedAt = entity.SysUpdatedAt,
            Mgdl = entity.Mgdl,
            Direction = Enum.TryParse<GlucoseDirection>(entity.Direction, out var dir) ? dir : null,
            TrendRate = entity.TrendRate,
            Noise = entity.Noise,
            Filtered = entity.Filtered,
            Unfiltered = entity.Unfiltered,
            Delta = entity.Delta,
            GlucoseProcessing = Enum.TryParse<GlucoseProcessing>(entity.GlucoseProcessing, out var gp) ? gp : null,
            SmoothedMgdl = entity.SmoothedMgdl,
            UnsmoothedMgdl = entity.UnsmoothedMgdl,
            AdditionalProperties = !string.IsNullOrEmpty(entity.AdditionalPropertiesJson)
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.AdditionalPropertiesJson)
                : null,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(SensorGlucoseEntity entity, SensorGlucose model)
    {
        entity.Timestamp = model.Timestamp;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.App = model.App;
        entity.DataSource = model.DataSource;
        entity.CorrelationId = model.CorrelationId;
        entity.PatientDeviceId = model.PatientDeviceId;
        entity.LegacyId = model.LegacyId;
        entity.SysUpdatedAt = DateTime.UtcNow;
        entity.Mgdl = model.Mgdl;
        entity.Direction = model.Direction?.ToString();
        entity.TrendRate = model.TrendRate;
        entity.Noise = model.Noise;
        entity.Filtered = model.Filtered;
        entity.Unfiltered = model.Unfiltered;
        entity.Delta = model.Delta;
        entity.GlucoseProcessing = model.GlucoseProcessing?.ToString();
        entity.SmoothedMgdl = model.SmoothedMgdl;
        entity.UnsmoothedMgdl = model.UnsmoothedMgdl;
        entity.AdditionalPropertiesJson = model.AdditionalProperties is { Count: > 0 }
            ? JsonSerializer.Serialize(model.AdditionalProperties)
            : null;
    }
}
