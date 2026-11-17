using System.Text.Json;
using Nocturne.Core.Models;
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
            Timestamp = model.StartTimestamp,
            EndTimestamp = model.EndTimestamp,
            UtcOffset = model.UtcOffset,
            Device = model.Device,
            App = model.App,
            DataSource = model.DataSource,
            CorrelationId = model.CorrelationId,
            LegacyId = model.LegacyId,
            Rate = model.Rate,
            ScheduledRate = model.ScheduledRate,
            Percent = model.Percent,
            RateFromSchedule = model.RateFromSchedule,
            Origin = model.Origin.ToString(),
            OriginEvidence = model.OriginEvidence?.ToString(),
            DeviceId = model.DeviceId,
            PatientDeviceId = model.PatientDeviceId,
            PumpRecordId = model.PumpRecordId,
            SyncIdentifier = model.SyncIdentifier,
            ApsSnapshotId = model.ApsSnapshotId,
            InsulinContextJson = model.InsulinContext is not null
                ? JsonSerializer.Serialize(model.InsulinContext)
                : null,
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
            StartTimestamp = entity.Timestamp,
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
            Percent = entity.Percent,
            RateFromSchedule = entity.RateFromSchedule,
            Origin = TempBasalOriginColumns.OriginOf(entity.Origin),
            OriginEvidence = TempBasalOriginColumns.EvidenceOf(entity.OriginEvidence),
            DeviceId = entity.DeviceId,
            PatientDeviceId = entity.PatientDeviceId,
            PumpRecordId = entity.PumpRecordId,
            SyncIdentifier = entity.SyncIdentifier,
            ApsSnapshotId = entity.ApsSnapshotId,
            InsulinContext = MapperHelpers.DeserializeJson<TreatmentInsulinContext>(
                entity.InsulinContextJson
            ),
            AdditionalProperties = MapperHelpers.DeserializeJson<Dictionary<string, object?>>(
                entity.AdditionalPropertiesJson
            ),
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    /// <param name="entity">The database entity to update.</param>
    /// <param name="model">The domain model containing updated data.</param>
    public static void UpdateEntity(TempBasalEntity entity, TempBasal model)
    {
        var origin = TempBasalOriginProvenance.OnUpdate(
            StoredOriginState(entity),
            new TempBasalOriginState(
                model.Origin, model.OriginEvidence, model.ApsSnapshotId, model.StartMills, model.Rate));
        entity.Timestamp = model.StartTimestamp;
        entity.EndTimestamp = model.EndTimestamp;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.App = model.App;
        entity.DataSource = model.DataSource;
        entity.CorrelationId = model.CorrelationId;
        entity.LegacyId = model.LegacyId;
        entity.Rate = model.Rate;
        entity.ScheduledRate = model.ScheduledRate;
        entity.Percent = model.Percent;
        entity.RateFromSchedule = model.RateFromSchedule;
        entity.Origin = origin.Origin.ToString();
        entity.OriginEvidence = origin.Evidence?.ToString();
        entity.ApsSnapshotId = origin.ApsSnapshotId;
        entity.DeviceId = model.DeviceId;
        entity.PatientDeviceId = model.PatientDeviceId;
        entity.PumpRecordId = model.PumpRecordId;
        entity.SyncIdentifier = model.SyncIdentifier;
        entity.InsulinContextJson = model.InsulinContext is not null
            ? JsonSerializer.Serialize(model.InsulinContext)
            : null;
        entity.AdditionalPropertiesJson = model.AdditionalProperties is { Count: > 0 }
            ? JsonSerializer.Serialize(model.AdditionalProperties)
            : null;
    }

    private static TempBasalOriginState StoredOriginState(TempBasalEntity entity)
    {
        var origin = TempBasalOriginColumns.OriginOf(entity.Origin);
        var evidence = TempBasalOriginProvenance.EvidenceOf(
            origin,
            TempBasalOriginColumns.EvidenceOf(entity.OriginEvidence),
            TempBasalAutomaticFlag.Of(
                MapperHelpers.DeserializeJson<Dictionary<string, object?>>(entity.AdditionalPropertiesJson)),
            entity.App,
            entity.DataSource,
            entity.LegacyId);
        return new TempBasalOriginState(origin, evidence, entity.ApsSnapshotId, entity.Timestamp.ToMills(), entity.Rate);
    }
}
