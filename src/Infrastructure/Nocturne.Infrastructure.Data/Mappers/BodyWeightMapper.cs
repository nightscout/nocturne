using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Common;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between BodyWeight domain models and BodyWeightEntity database entities
/// </summary>
public static class BodyWeightMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static BodyWeightEntity ToEntity(BodyWeight bodyWeight)
    {
        return new BodyWeightEntity
        {
            Id = string.IsNullOrEmpty(bodyWeight.Id)
                ? Guid.CreateVersion7()
                : ParseIdToGuid(bodyWeight.Id),
            OriginalId = MongoIdUtils.IsValidMongoId(bodyWeight.Id) ? bodyWeight.Id : null,
            Mills = bodyWeight.Mills,
            WeightKg = bodyWeight.WeightKg,
            BodyFatPercent = bodyWeight.BodyFatPercent,
            LeanMassKg = bodyWeight.LeanMassKg,
            Device = bodyWeight.Device,
            EnteredBy = bodyWeight.EnteredBy,
            CreatedAt = bodyWeight.CreatedAt,
            UtcOffset = bodyWeight.UtcOffset,
            DataSource = bodyWeight.DataSource,
            SyncIdentifier = bodyWeight.SyncIdentifier,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static BodyWeight ToDomainModel(BodyWeightEntity entity)
    {
        return new BodyWeight
        {
            Id = entity.OriginalId ?? entity.Id.ToString(),
            Mills = entity.Mills,
            WeightKg = entity.WeightKg,
            BodyFatPercent = entity.BodyFatPercent,
            LeanMassKg = entity.LeanMassKg,
            Device = entity.Device,
            EnteredBy = entity.EnteredBy,
            CreatedAt = entity.CreatedAt,
            UtcOffset = entity.UtcOffset,
            DataSource = entity.DataSource,
            SyncIdentifier = entity.SyncIdentifier,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(BodyWeightEntity entity, BodyWeight bodyWeight)
    {
        entity.Mills = bodyWeight.Mills;
        entity.WeightKg = bodyWeight.WeightKg;
        entity.BodyFatPercent = bodyWeight.BodyFatPercent;
        entity.LeanMassKg = bodyWeight.LeanMassKg;
        entity.Device = bodyWeight.Device;
        entity.EnteredBy = bodyWeight.EnteredBy;
        entity.CreatedAt = bodyWeight.CreatedAt;
        entity.UtcOffset = bodyWeight.UtcOffset;
        entity.DataSource = bodyWeight.DataSource;
        entity.SyncIdentifier = bodyWeight.SyncIdentifier;
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
