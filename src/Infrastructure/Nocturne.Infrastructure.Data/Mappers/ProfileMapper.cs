using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Common;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Mapper for converting between Profile domain models and ProfileEntity database entities
/// </summary>
public static class ProfileMapper
{
    /// <summary>
    /// Convert domain model to database entity
    /// </summary>
    public static ProfileEntity ToEntity(Profile profile)
    {
        // Set created_at to now if not provided (Nightscout behavior)
        var createdAt = string.IsNullOrEmpty(profile.CreatedAt)
            ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            : profile.CreatedAt;

        return new ProfileEntity
        {
            Id = string.IsNullOrEmpty(profile.Id)
                ? Guid.CreateVersion7()
                : ParseIdToGuid(profile.Id),
            OriginalId = MongoIdUtils.IsValidMongoId(profile.Id) ? profile.Id : null,
            DefaultProfile = profile.DefaultProfile,
            StartDate = profile.StartDate,
            Mills = profile.Mills,
            CreatedAt = createdAt,
            Units = profile.Units,
            StoreJson = profile.Store != null ? JsonSerializer.Serialize(profile.Store) : "{}",
            EnteredBy = profile.EnteredBy,
            LoopSettingsJson = profile.LoopSettings != null
                ? JsonSerializer.Serialize(profile.LoopSettings)
                : null,
            UpdatedAtPg = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Convert database entity to domain model
    /// </summary>
    public static Profile ToDomainModel(ProfileEntity entity)
    {
        var store =
            string.IsNullOrEmpty(entity.StoreJson) || entity.StoreJson == "{}"
                ? new Dictionary<string, ProfileData>()
                : JsonSerializer.Deserialize<Dictionary<string, ProfileData>>(entity.StoreJson)
                    ?? new Dictionary<string, ProfileData>();

        // Ensure timeAsSeconds is calculated for all schedule entries
        foreach (var profileData in store.Values)
        {
            CalculateTimeAsSeconds(profileData.Basal);
            CalculateTimeAsSeconds(profileData.CarbRatio);
            CalculateTimeAsSeconds(profileData.Sens);
            CalculateTimeAsSeconds(profileData.TargetLow);
            CalculateTimeAsSeconds(profileData.TargetHigh);
        }

        LoopProfileSettings? loopSettings = null;
        if (!string.IsNullOrEmpty(entity.LoopSettingsJson))
        {
            loopSettings = JsonSerializer.Deserialize<LoopProfileSettings>(entity.LoopSettingsJson);
        }

        return new Profile
        {
            Id = entity.OriginalId ?? entity.Id.ToString(),
            DefaultProfile = entity.DefaultProfile,
            StartDate = entity.StartDate,
            Mills = entity.Mills,
            CreatedAt = entity.CreatedAt,
            Units = entity.Units,
            Store = store,
            EnteredBy = entity.EnteredBy,
            LoopSettings = loopSettings,
        };
    }

    /// <summary>
    /// Calculate timeAsSeconds for a list of TimeValue entries
    /// </summary>
    private static void CalculateTimeAsSeconds(List<TimeValue>? timeValues)
    {
        if (timeValues == null) return;

        foreach (var tv in timeValues)
        {
            tv.EnsureTimeAsSeconds();
        }
    }

    /// <summary>
    /// Update existing entity with values from domain model
    /// </summary>
    public static void UpdateEntity(ProfileEntity entity, Profile profile)
    {
        entity.DefaultProfile = profile.DefaultProfile;
        entity.StartDate = profile.StartDate;
        entity.Mills = profile.Mills;
        entity.CreatedAt = profile.CreatedAt;
        entity.Units = profile.Units;
        entity.StoreJson = profile.Store != null ? JsonSerializer.Serialize(profile.Store) : "{}";
        entity.EnteredBy = profile.EnteredBy;
        entity.LoopSettingsJson = profile.LoopSettings != null
            ? JsonSerializer.Serialize(profile.LoopSettings)
            : null;
        entity.UpdatedAtPg = DateTime.UtcNow;
    }

    /// <summary>
    /// Parse string ID to GUID
    /// </summary>
    private static Guid ParseIdToGuid(string id)
    {
        if (string.IsNullOrEmpty(id))
            return Guid.CreateVersion7();

        // Try to parse as GUID first
        if (Guid.TryParse(id, out var guid))
            return guid;

        // For 24-character hex strings, use a deterministic conversion
        if (id.Length == 24)
        {
            // Convert hex string to bytes and then to GUID
            var bytes = new byte[16];
            for (int i = 0; i < 12; i++)
            {
                bytes[i] = Convert.ToByte(id.Substring(i * 2, 2), 16);
            }
            // Fill remaining bytes with zeros
            return new Guid(bytes);
        }

        // Fallback: generate new GUID
        return Guid.CreateVersion7();
    }
}
