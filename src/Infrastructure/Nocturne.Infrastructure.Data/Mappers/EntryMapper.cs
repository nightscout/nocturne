using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Common;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between Entry domain models and EntryEntity database entities
/// </summary>
public static class EntryMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static EntryEntity ToEntity(Entry entry)
    {
        return new EntryEntity
        {
            Id = string.IsNullOrEmpty(entry.Id) ? Guid.CreateVersion7() : ParseIdToGuid(entry.Id),
            OriginalId = MongoIdUtils.IsValidMongoId(entry.Id) ? entry.Id : null,
            Mills = entry.Mills,
            DateString = entry.DateString,
            Mgdl = entry.Mgdl,
            Mmol = entry.Mmol,
            Sgv = entry.Sgv,
            Direction = entry.Direction,
            Trend = entry.Trend,
            TrendRate = entry.TrendRate,
            IsCalibration = entry.IsCalibration,
            Type = entry.Type,
            Device = entry.Device,
            Notes = entry.Notes,
            Delta = entry.Delta,
            ScaledJson = entry.Scaled != null ? JsonSerializer.Serialize(entry.Scaled) : null,
            SysTime = entry.SysTime,
            UtcOffset = entry.UtcOffset,
            Noise = entry.Noise,
            Filtered = entry.Filtered,
            Unfiltered = entry.Unfiltered,
            Rssi = entry.Rssi,
            Slope = entry.Slope,
            Intercept = entry.Intercept,
            Scale = entry.Scale,
            ModifiedAt = entry.ModifiedAt,
            DataSource = entry.DataSource,
            MetaJson = entry.Meta != null ? JsonSerializer.Serialize(entry.Meta) : null,
            App = entry.App,
            Units = entry.Units,
            IsValid = entry.IsValid,
            IsReadOnly = entry.IsReadOnly,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static Entry ToDomainModel(EntryEntity entity)
    {
        return new Entry
        {
            Id = entity.OriginalId ?? entity.Id.ToString(),
            Mills = entity.Mills,
            DateString = entity.DateString,
            Mgdl = entity.Mgdl,
            Mmol = entity.Mmol,
            Sgv = entity.Sgv,
            Direction = entity.Direction,
            Trend = entity.Trend,
            TrendRate = entity.TrendRate,
            IsCalibration = entity.IsCalibration,
            Type = entity.Type,
            Device = entity.Device,
            Notes = entity.Notes,
            Delta = entity.Delta,
            Scaled = DeserializeJsonProperty(entity.ScaledJson),
            SysTime = entity.SysTime,
            UtcOffset = entity.UtcOffset,
            Noise = entity.Noise,
            Filtered = entity.Filtered,
            Unfiltered = entity.Unfiltered,
            Rssi = entity.Rssi,
            Slope = entity.Slope,
            Intercept = entity.Intercept,
            Scale = entity.Scale,
            CreatedAt = entity.CreatedAt,
            ModifiedAt = entity.ModifiedAt,
            DataSource = entity.DataSource,
            Meta = DeserializeJsonProperty<Dictionary<string, object>>(entity.MetaJson),
            App = entity.App,
            Units = entity.Units,
            IsValid = entity.IsValid,
            IsReadOnly = entity.IsReadOnly,
            SrvModified =
                entity.SysUpdatedAt != default
                    ? new DateTimeOffset(
                        entity.SysUpdatedAt,
                        TimeSpan.Zero
                    ).ToUnixTimeMilliseconds()
                    : null,
            SrvCreated =
                entity.SysCreatedAt != default
                    ? new DateTimeOffset(
                        entity.SysCreatedAt,
                        TimeSpan.Zero
                    ).ToUnixTimeMilliseconds()
                    : null,
        };
    }

    /// <summary>
    /// Update existing entity with data from domain model
    /// </summary>
    public static void UpdateEntity(EntryEntity entity, Entry entry)
    {
        entity.Mills = entry.Mills;
        entity.DateString = entry.DateString;
        entity.Mgdl = entry.Mgdl;
        entity.Mmol = entry.Mmol;
        entity.Sgv = entry.Sgv;
        entity.Direction = entry.Direction;
        entity.Trend = entry.Trend;
        entity.TrendRate = entry.TrendRate;
        entity.IsCalibration = entry.IsCalibration;
        entity.Type = entry.Type;
        entity.Device = entry.Device;
        entity.Notes = entry.Notes;
        entity.Delta = entry.Delta;
        entity.ScaledJson = entry.Scaled != null ? JsonSerializer.Serialize(entry.Scaled) : null;
        entity.SysTime = entry.SysTime;
        entity.UtcOffset = entry.UtcOffset;
        entity.Noise = entry.Noise;
        entity.Filtered = entry.Filtered;
        entity.Unfiltered = entry.Unfiltered;
        entity.Rssi = entry.Rssi;
        entity.Slope = entry.Slope;
        entity.Intercept = entry.Intercept;
        entity.Scale = entry.Scale;
        entity.CreatedAt = entry.CreatedAt;
        entity.ModifiedAt = entry.ModifiedAt;
        entity.DataSource = entry.DataSource;
        entity.MetaJson = entry.Meta != null ? JsonSerializer.Serialize(entry.Meta) : null;
        entity.App = entry.App;
        entity.Units = entry.Units;
        entity.IsValid = entry.IsValid;
        entity.IsReadOnly = entry.IsReadOnly;
    }

    /// <summary>
    /// Parse string ID to GUID, or generate new GUID if invalid
    /// </summary>
    private static Guid ParseIdToGuid(string id)
    {
        // Hash the ID to get a deterministic GUID for consistent mapping
        if (string.IsNullOrEmpty(id))
            return Guid.CreateVersion7();

        if (Guid.TryParse(id, out var parsedGuid))
        {
            return parsedGuid;
        }

        try
        {
            // Use a simple hash of the ID to generate a consistent GUID
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            var hashBytes = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(id));
            var guidBytes = new byte[16];
            Array.Copy(hashBytes, guidBytes, 16);
            return new Guid(guidBytes);
        }
        catch
        {
            // If anything fails, generate a new GUID
            return Guid.CreateVersion7();
        }
    }

    /// <summary>
    /// Safely deserialize JSON property
    /// </summary>
    private static T? DeserializeJsonProperty<T>(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null")
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Safely deserialize JSON property as object
    /// </summary>
    private static object? DeserializeJsonProperty(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null")
            return null;

        try
        {
            return JsonSerializer.Deserialize<object>(json);
        }
        catch
        {
            return null;
        }
    }
}
