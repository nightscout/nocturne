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
    public static SensorGlucoseEntity ToEntity(SensorGlucose model)
    {
        return new SensorGlucoseEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            Mills = model.Mills,
            UtcOffset = model.UtcOffset,
            Device = model.Device,
            App = model.App,
            DataSource = model.DataSource,
            CorrelationId = model.CorrelationId,
            LegacyId = model.LegacyId,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
            Mgdl = model.Mgdl,
            Mmol = model.Mmol,
            Direction = model.Direction?.ToString(),
            Trend = model.Trend.HasValue ? (int)model.Trend.Value : null,
            TrendRate = model.TrendRate,
            Noise = model.Noise,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static SensorGlucose ToDomainModel(SensorGlucoseEntity entity)
    {
        return new SensorGlucose
        {
            Id = entity.Id,
            Mills = entity.Mills,
            UtcOffset = entity.UtcOffset,
            Device = entity.Device,
            App = entity.App,
            DataSource = entity.DataSource,
            CorrelationId = entity.CorrelationId,
            LegacyId = entity.LegacyId,
            CreatedAt = entity.SysCreatedAt,
            ModifiedAt = entity.SysUpdatedAt,
            Mgdl = entity.Mgdl,
            Mmol = entity.Mmol,
            Direction = Enum.TryParse<GlucoseDirection>(entity.Direction, out var dir) ? dir : null,
            Trend = entity.Trend.HasValue ? (GlucoseTrend)entity.Trend.Value : null,
            TrendRate = entity.TrendRate,
            Noise = entity.Noise,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(SensorGlucoseEntity entity, SensorGlucose model)
    {
        entity.Mills = model.Mills;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.App = model.App;
        entity.DataSource = model.DataSource;
        entity.CorrelationId = model.CorrelationId;
        entity.LegacyId = model.LegacyId;
        entity.SysUpdatedAt = DateTime.UtcNow;
        entity.Mgdl = model.Mgdl;
        entity.Mmol = model.Mmol;
        entity.Direction = model.Direction?.ToString();
        entity.Trend = model.Trend.HasValue ? (int)model.Trend.Value : null;
        entity.TrendRate = model.TrendRate;
        entity.Noise = model.Noise;
    }
}
