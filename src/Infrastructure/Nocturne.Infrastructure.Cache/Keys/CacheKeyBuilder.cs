namespace Nocturne.Infrastructure.Cache.Keys;

/// <summary>
/// Cache key builder for generating consistent cache keys
/// </summary>
public static class CacheKeyBuilder
{
    private const string KeySeparator = ":";

    /// <summary>
    /// Builds a cache key for current entries
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="suffix">Optional suffix</param>
    public static string BuildCurrentEntriesKey(string tenantId, string? suffix = null) =>
        BuildKey("entries", "current", tenantId, suffix);

    /// <summary>
    /// Builds a cache key for current profile
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    public static string BuildCurrentProfileKey(string tenantId) =>
        BuildKey("profiles", "current", tenantId);

    /// <summary>
    /// Builds a cache key for recent entries with count and type filters
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="count">Number of entries requested</param>
    /// <param name="type">Entry type filter (e.g., "sgv", "mbg", "cal")</param>
    /// <param name="skip">Number of entries to skip</param>
    public static string BuildRecentEntriesKey(
        string tenantId,
        int count,
        string? type = null,
        int skip = 0
    )
    {
        var keyParts = new List<string> { "entries", "recent", tenantId, count.ToString() };

        if (!string.IsNullOrEmpty(type))
        {
            keyParts.Add($"type:{type}");
        }

        if (skip > 0)
        {
            keyParts.Add($"skip:{skip}");
        }

        return string.Join(KeySeparator, keyParts);
    }

    /// <summary>
    /// Builds a cache key for recent treatments with time range
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="hours">Time range in hours (e.g., 12, 24, 48)</param>
    /// <param name="count">Number of treatments requested</param>
    /// <param name="skip">Number of treatments to skip</param>
    public static string BuildRecentTreatmentsKey(
        string tenantId,
        int hours,
        int count = 10,
        int skip = 0
    )
    {
        var keyParts = new List<string> { "treatments", "recent", tenantId, $"{hours}h" };

        if (count != 10) // Only add if not default
        {
            keyParts.Add($"count:{count}");
        }

        if (skip > 0)
        {
            keyParts.Add($"skip:{skip}");
        }

        return string.Join(KeySeparator, keyParts);
    }

    /// <summary>
    /// Builds a generic cache key
    /// </summary>
    /// <param name="category">Cache category</param>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="parts">Additional key parts</param>
    public static string BuildKey(string category, string tenantId, params string?[] parts)
    {
        var keyParts = new List<string> { category, tenantId };

        foreach (var part in parts)
        {
            if (!string.IsNullOrEmpty(part))
            {
                keyParts.Add(part);
            }
        }

        return string.Join(KeySeparator, keyParts);
    }

    /// <summary>
    /// Creates a pattern for cache key matching
    /// </summary>
    /// <param name="category">Cache category</param>
    /// <param name="tenantId">Tenant ID (or * for all tenants)</param>
    /// <param name="pattern">Pattern suffix</param>
    public static string BuildPattern(
        string category,
        string tenantId = "*",
        string pattern = "*"
    ) => $"{category}{KeySeparator}{tenantId}{KeySeparator}{pattern}";

    /// <summary>
    /// Creates a pattern for invalidating all recent entries cache
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    public static string BuildRecentEntriesPattern(string tenantId) =>
        $"entries{KeySeparator}recent{KeySeparator}{tenantId}{KeySeparator}*";

    /// <summary>
    /// Creates a pattern for invalidating all recent treatments cache
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    public static string BuildRecentTreatmentsPattern(string tenantId) =>
        $"treatments{KeySeparator}recent{KeySeparator}{tenantId}{KeySeparator}*";

    /// <summary>
    /// Creates a pattern for invalidating all profile timestamp cache
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    public static string BuildProfileTimestampPattern(string tenantId) =>
        $"profiles{KeySeparator}at{KeySeparator}{tenantId}{KeySeparator}*";

    #region Expensive Calculation Cache Keys

    /// <summary>
    /// Builds a cache key for IOB calculation results
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="timestamp">Timestamp for calculation</param>
    public static string BuildIobCalculationKey(string userId, long timestamp) =>
        BuildKey("calculations", "iob", userId, timestamp.ToString());

    /// <summary>
    /// Builds a cache key for COB calculation results
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="timestamp">Timestamp for calculation</param>
    public static string BuildCobCalculationKey(string userId, long timestamp) =>
        BuildKey("calculations", "cob", userId, timestamp.ToString());

    /// <summary>
    /// Builds a cache key for profile calculations at timestamp
    /// </summary>
    /// <param name="profileId">Profile ID</param>
    /// <param name="timestamp">Timestamp for calculation</param>
    public static string BuildProfileCalculatedKey(string profileId, long timestamp) =>
        BuildKey("profiles", "calculated", profileId, timestamp.ToString());

    /// <summary>
    /// Creates a pattern for invalidating all IOB calculation cache for a user
    /// </summary>
    /// <param name="userId">User ID</param>
    public static string BuildIobCalculationPattern(string userId) =>
        $"calculations{KeySeparator}iob{KeySeparator}{userId}{KeySeparator}*";

    /// <summary>
    /// Creates a pattern for invalidating all COB calculation cache for a user
    /// </summary>
    /// <param name="userId">User ID</param>
    public static string BuildCobCalculationPattern(string userId) =>
        $"calculations{KeySeparator}cob{KeySeparator}{userId}{KeySeparator}*";

    /// <summary>
    /// Creates a pattern for invalidating all statistics cache for a user
    /// </summary>
    /// <param name="userId">User ID</param>
    public static string BuildStatsPattern(string userId) =>
        $"stats{KeySeparator}*{KeySeparator}{userId}{KeySeparator}*";

    /// <summary>
    /// Creates a pattern for invalidating all profile calculated cache for a profile
    /// </summary>
    /// <param name="profileId">Profile ID</param>
    public static string BuildProfileCalculatedPattern(string profileId) =>
        $"profiles{KeySeparator}calculated{KeySeparator}{profileId}{KeySeparator}*";

    #endregion
}
