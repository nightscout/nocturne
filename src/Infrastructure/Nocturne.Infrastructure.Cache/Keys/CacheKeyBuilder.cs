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
}
