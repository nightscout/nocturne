namespace Nocturne.Infrastructure.Cache.Constants;

/// <summary>
/// Constants for cache-related magic strings and values
/// </summary>
public static class CacheConstants
{
    /// <summary>
    /// Default configuration values
    /// </summary>
    public static class Defaults
    {
        public const int CurrentEntryExpirationSeconds = 60; // 1 minute
        public const int RecentEntriesExpirationSeconds = 120; // 2 minutes
        public const int RecentTreatmentsExpirationSeconds = 300; // 5 minutes
    }
}
