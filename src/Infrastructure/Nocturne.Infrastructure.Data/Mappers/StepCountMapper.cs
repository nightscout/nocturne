using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Common;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between StepCount domain models and StepCountEntity database entities
/// </summary>
public static class StepCountMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static StepCountEntity ToEntity(StepCount stepCount)
    {
        return new StepCountEntity
        {
            Id = string.IsNullOrEmpty(stepCount.Id)
                ? Guid.CreateVersion7()
                : ParseIdToGuid(stepCount.Id),
            OriginalId = MongoIdUtils.IsValidMongoId(stepCount.Id) ? stepCount.Id : null,
            Timestamp = stepCount.Timestamp,
            Metric = stepCount.Metric,
            Source = stepCount.Source,
            Device = stepCount.Device,
            EnteredBy = stepCount.EnteredBy,
            UtcOffset = stepCount.UtcOffset,
            DataSource = stepCount.DataSource,
            SyncIdentifier = stepCount.SyncIdentifier,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static StepCount ToDomainModel(StepCountEntity entity)
    {
        return new StepCount
        {
            Id = entity.OriginalId ?? entity.Id.ToString(),
            Timestamp = entity.Timestamp,
            Metric = entity.Metric,
            Source = entity.Source,
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
    public static void UpdateEntity(StepCountEntity entity, StepCount stepCount)
    {
        entity.Timestamp = stepCount.Timestamp;
        entity.Metric = stepCount.Metric;
        entity.Source = stepCount.Source;
        entity.Device = stepCount.Device;
        entity.EnteredBy = stepCount.EnteredBy;
        entity.UtcOffset = stepCount.UtcOffset;
        entity.DataSource = stepCount.DataSource;
        entity.SyncIdentifier = stepCount.SyncIdentifier;
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
