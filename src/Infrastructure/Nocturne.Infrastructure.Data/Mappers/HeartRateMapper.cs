using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Common;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between HeartRate domain models and HeartRateEntity database entities
/// </summary>
public static class HeartRateMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static HeartRateEntity ToEntity(HeartRate heartRate)
    {
        return new HeartRateEntity
        {
            Id = string.IsNullOrEmpty(heartRate.Id)
                ? Guid.CreateVersion7()
                : ParseIdToGuid(heartRate.Id),
            OriginalId = MongoIdUtils.IsValidMongoId(heartRate.Id) ? heartRate.Id : null,
            Timestamp = heartRate.Timestamp,
            Bpm = heartRate.Bpm,
            Accuracy = heartRate.Accuracy,
            Device = heartRate.Device,
            EnteredBy = heartRate.EnteredBy,
            UtcOffset = heartRate.UtcOffset,
            DataSource = heartRate.DataSource,
            SyncIdentifier = heartRate.SyncIdentifier,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static HeartRate ToDomainModel(HeartRateEntity entity)
    {
        return new HeartRate
        {
            Id = entity.OriginalId ?? entity.Id.ToString(),
            Timestamp = entity.Timestamp,
            Bpm = entity.Bpm,
            Accuracy = entity.Accuracy,
            Device = entity.Device,
            EnteredBy = entity.EnteredBy,
            UtcOffset = entity.UtcOffset,
            DataSource = entity.DataSource,
            SyncIdentifier = entity.SyncIdentifier,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(HeartRateEntity entity, HeartRate heartRate)
    {
        entity.Timestamp = heartRate.Timestamp;
        entity.Bpm = heartRate.Bpm;
        entity.Accuracy = heartRate.Accuracy;
        entity.Device = heartRate.Device;
        entity.EnteredBy = heartRate.EnteredBy;
        entity.UtcOffset = heartRate.UtcOffset;
        entity.DataSource = heartRate.DataSource;
        entity.SyncIdentifier = heartRate.SyncIdentifier;
        entity.SysUpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Parse string ID to GUID, or generate a deterministic GUID via hash if invalid
    /// </summary>
    private static Guid ParseIdToGuid(string id)
    {
        if (string.IsNullOrEmpty(id))
            return Guid.CreateVersion7();

        if (Guid.TryParse(id, out var guid))
            return guid;

        var hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(id)
        );
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        return new Guid(guidBytes);
    }
}
