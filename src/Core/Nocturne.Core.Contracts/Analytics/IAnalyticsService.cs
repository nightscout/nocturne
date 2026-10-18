using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Analytics;

/// <summary>
/// Interface for privacy-first analytics collection service
/// Collects anonymous usage data to help improve Nocturne while respecting user privacy
/// </summary>
public interface IAnalyticsService
{
    /// <summary>
    /// Track an API endpoint call with anonymous usage data
    /// </summary>
    /// <param name="endpoint">API endpoint path (e.g., "/api/v1/entries")</param>
    /// <param name="method">HTTP method (GET, POST, etc.)</param>
    /// <param name="responseTime">Response time in milliseconds</param>
    /// <param name="statusCode">HTTP status code</param>
    Task TrackApiCallAsync(
        string endpoint,
        string method,
        long responseTime,
        int statusCode,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Track a page view or UI navigation event
    /// </summary>
    /// <param name="page">Page identifier (e.g., "dashboard", "reports")</param>
    /// <param name="userAgent">User agent string for device type detection</param>
    Task TrackPageViewAsync(
        string page,
        string? userAgent = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Track feature usage (e.g., connector activation, report generation)
    /// </summary>
    /// <param name="feature">Feature name</param>
    /// <param name="action">Action taken (e.g., "enabled", "disabled", "used")</param>
    /// <param name="metadata">Additional anonymous metadata</param>
    Task TrackFeatureUsageAsync(
        string feature,
        string action,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Track system events (startup, shutdown, errors)
    /// </summary>
    /// <param name="message">Anonymous event message</param>
    /// <param name="severity">Event severity level</param>
    Task TrackSystemEventAsync(
        string eventType,
        string message,
        string severity = "info",
        CancellationToken cancellationToken = default
    );

    Task TrackCustomEventAsync(
        AnalyticsEvent analyticsEvent,
        CancellationToken cancellationToken = default
    );

    /// <returns>Anonymous system information</returns>
    SystemInfo GetSystemInfo();

    /// <summary>
    /// Get performance metrics for the current time period
    /// </summary>
    PerformanceMetrics GetPerformanceMetrics();

    /// <summary>
    /// Get usage statistics for the current time period
    /// </summary>
    UsageStatistics GetUsageStatistics();

    /// <summary>
    /// Check if analytics collection is enabled
    /// </summary>
    /// <returns>True if analytics collection is enabled</returns>
    bool IsAnalyticsEnabled();

    /// <returns>Analytics collection configuration</returns>
    AnalyticsCollectionConfig GetAnalyticsConfig();

    /// <param name="config">New configuration</param>
    Task UpdateAnalyticsConfigAsync(AnalyticsCollectionConfig config);

    /// <summary>
    /// Get analytics data that would be transmitted (for transparency)
    /// </summary>
    /// <returns>Current analytics data pending transmission</returns>
    Task<AnalyticsBatch?> GetPendingAnalyticsDataAsync();

    Task ClearAnalyticsDataAsync();
}
