using System.Text.Json;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers.V4;

/// <summary>
/// Mapper for converting between Bolus domain models and BolusEntity database entities
/// </summary>
public static class BolusMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    /// <param name="model">The domain model to convert.</param>
    /// <returns>A new instance of BolusEntity.</returns>
    public static BolusEntity ToEntity(Bolus model)
    {
        return new BolusEntity
        {
            Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id,
            Timestamp = model.Timestamp,
            UtcOffset = model.UtcOffset,
            Device = model.Device,
            App = model.App,
            DataSource = model.DataSource,
            CorrelationId = model.CorrelationId,
            LegacyId = model.LegacyId,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
            Insulin = model.Insulin,
            Programmed = model.Programmed,
            Delivered = model.Delivered,
            BolusType = model.BolusType?.ToString(),
            Automatic = model.Automatic,
            BolusKind = model.Kind.ToString(),
            Duration = model.Duration,
            SyncIdentifier = model.SyncIdentifier,
            InsulinType = model.InsulinType,
            InsulinContextJson = model.InsulinContext is not null
                ? JsonSerializer.Serialize(model.InsulinContext)
                : null,
            Unabsorbed = model.Unabsorbed,
            DeviceId = model.DeviceId,
            PumpRecordId = model.PumpRecordId,
            BolusCalculationId = model.BolusCalculationId,
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
    /// <returns>A new instance of Bolus domain model.</returns>
    public static Bolus ToDomainModel(BolusEntity entity)
    {
        return new Bolus
        {
            Id = entity.Id,
            Timestamp = entity.Timestamp,
            UtcOffset = entity.UtcOffset,
            Device = entity.Device,
            App = entity.App,
            DataSource = entity.DataSource,
            CorrelationId = entity.CorrelationId,
            LegacyId = entity.LegacyId,
            CreatedAt = entity.SysCreatedAt,
            ModifiedAt = entity.SysUpdatedAt,
            Insulin = entity.Insulin,
            Programmed = entity.Programmed,
            Delivered = entity.Delivered,
            BolusType = Enum.TryParse<BolusType>(entity.BolusType, out var bt) ? bt : null,
            Automatic = entity.Automatic,
            Kind = Enum.TryParse<BolusKind>(entity.BolusKind, out var kind) ? kind : BolusKind.Manual,
            Duration = entity.Duration,
            SyncIdentifier = entity.SyncIdentifier,
            InsulinType = entity.InsulinType,
            InsulinContext = !string.IsNullOrEmpty(entity.InsulinContextJson)
                ? JsonSerializer.Deserialize<TreatmentInsulinContext>(entity.InsulinContextJson)
                : null,
            Unabsorbed = entity.Unabsorbed,
            DeviceId = entity.DeviceId,
            PumpRecordId = entity.PumpRecordId,
            BolusCalculationId = entity.BolusCalculationId,
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
    public static void UpdateEntity(BolusEntity entity, Bolus model)
    {
        entity.Timestamp = model.Timestamp;
        entity.UtcOffset = model.UtcOffset;
        entity.Device = model.Device;
        entity.App = model.App;
        entity.DataSource = model.DataSource;
        entity.CorrelationId = model.CorrelationId;
        entity.LegacyId = model.LegacyId;
        entity.SysUpdatedAt = DateTime.UtcNow;
        entity.Insulin = model.Insulin;
        entity.Programmed = model.Programmed;
        entity.Delivered = model.Delivered;
        entity.BolusType = model.BolusType?.ToString();
        entity.Automatic = model.Automatic;
        entity.BolusKind = model.Kind.ToString();
        entity.Duration = model.Duration;
        entity.SyncIdentifier = model.SyncIdentifier;
        entity.InsulinType = model.InsulinType;
        entity.InsulinContextJson = model.InsulinContext is not null
            ? JsonSerializer.Serialize(model.InsulinContext)
            : null;
        entity.Unabsorbed = model.Unabsorbed;
        entity.DeviceId = model.DeviceId;
        entity.PumpRecordId = model.PumpRecordId;
        entity.BolusCalculationId = model.BolusCalculationId;
        entity.ApsSnapshotId = model.ApsSnapshotId;
        entity.AdditionalPropertiesJson = model.AdditionalProperties is { Count: > 0 }
            ? JsonSerializer.Serialize(model.AdditionalProperties)
            : null;
    }
}
