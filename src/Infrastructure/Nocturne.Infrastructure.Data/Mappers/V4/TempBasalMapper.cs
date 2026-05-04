using System.Text.Json;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between TempBasal domain models and TempBasalEntity database entities
/// </summary>
public static class TempBasalMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of TempBasalEntity.</returns>
    public static TempBasalEntity ToEntity(TempBasal model)
    {
        return new TempBasalEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            StartTimestamp = model.StartTimestamp,
            EndTimestamp = model.EndTimestamp,
            UtcOffset = model.UtcOffset,
            Device = model.Device,
            App = model.App,
            DataSource = model.DataSource,
            CorrelationId = model.CorrelationId,
            LegacyId = model.LegacyId,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
            Rate = model.Rate,
            ScheduledRate = model.ScheduledRate,
            Origin = model.Origin.ToString(),
            DeviceId = model.DeviceId,
            PatientDeviceId = model.PatientDeviceId,
            PumpRecordId = model.PumpRecordId,
            ApsSnapshotId = model.ApsSnapshotId,
            AdditionalPropertiesJson = model.AdditionalProperties is { Count: > 0 }
                ? JsonSerializer.Serialize(model.AdditionalProperties)
                : null,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    /// <param name="entity">The database entity to convert.</param>
    /// <returns>A new instance of TempBasal domain model.</returns>
    public static TempBasal ToDomainModel(TempBasalEntity entity)
    {
        return new TempBasal
        {
            Id = entity.Id,
            StartTimestamp = entity.StartTimestamp,
            EndTimestamp = entity.EndTimestamp,
            UtcOffset = entity.UtcOffset,
            Device = entity.Device,
            App = entity.App,
            DataSource = entity.DataSource,
            CorrelationId = entity.CorrelationId,
            LegacyId = entity.LegacyId,
            CreatedAt = entity.SysCreatedAt,
            ModifiedAt = entity.SysUpdatedAt,
            Rate = entity.Rate,
            ScheduledRate = entity.ScheduledRate,
            Origin = Enum.TryParse<TempBasalOrigin>(entity.Origin, out var origin)
                ? origin
                : TempBasalOrigin.Inferred,
            DeviceId = entity.DeviceId,
            PatientDeviceId = entity.PatientDeviceId,
            PumpRecordId = entity.PumpRecordId,
            ApsSnapshotId = entity.ApsSnapshotId,
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
    public static void UpdateEntity(TempBasalEntity entity, TempBasal model)
    {
        entity.StartTimestamp = model.StartTimestamp;
        entity.EndTimestamp = model.EndTimestamp;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.App = model.App;
        entity.DataSource = model.DataSource;
        entity.CorrelationId = model.CorrelationId;
        entity.LegacyId = model.LegacyId;
        entity.SysUpdatedAt = DateTime.UtcNow;
        entity.Rate = model.Rate;
        entity.ScheduledRate = model.ScheduledRate;
        entity.Origin = model.Origin.ToString();
        entity.DeviceId = model.DeviceId;
        entity.PatientDeviceId = model.PatientDeviceId;
        entity.PumpRecordId = model.PumpRecordId;
        entity.ApsSnapshotId = model.ApsSnapshotId;
        entity.AdditionalPropertiesJson = model.AdditionalProperties is { Count: > 0 }
            ? JsonSerializer.Serialize(model.AdditionalProperties)
            : null;
    }
}
