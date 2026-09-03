namespace Nocturne.Infrastructure.Cache.Constants;

/// <summary>
/// Constants for cache-related magic strings and values
/// </summary>
public static class CacheConstants
{
    /// <summary>
    /// Processing status values
    /// </summary>
    public static class ProcessingStatus
    {
        public const string Pending = "pending";
        public const string Processing = "processing";
        public const string Completed = "completed";
        public const string Failed = "failed";
    }

    /// <summary>
    /// Default configuration values
    /// </summary>
    public static class Defaults
    {
        public const int CurrentEntryExpirationSeconds = 60; // 1 minute
        public const int RecentEntriesExpirationSeconds = 120; // 2 minutes
        public const int RecentTreatmentsExpirationSeconds = 300; // 5 minutes
    }

    /// <summary>
    /// Cache key prefixes
    /// </summary>
    public static class KeyPrefixes
    {
        public const string System = "system";
    }

    /// <summary>
    /// Common entry types
    /// </summary>
    public static class EntryTypes
    {
        public const string Sgv = "sgv";
    }

    /// <summary>
    /// Cleanup intervals
    /// </summary>
    public static class CleanupIntervals
    {
        public static readonly TimeSpan StatusCleanup = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Default TTL values
    /// </summary>
    public static class DefaultTtl
    {
        public static readonly TimeSpan ProcessingStatus = TimeSpan.FromHours(1);
        public static readonly TimeSpan SystemLookups = TimeSpan.FromHours(4);
        public static readonly TimeSpan SystemStatus = TimeSpan.FromMinutes(5);
    }
}
