using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Anonymous analytics event data - contains no personal or medical information
/// </summary>
public class AnalyticsEvent
{
    /// <summary>
    /// Anonymous session identifier (generated locally, not persistent across restarts)
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Event type (e.g., "api_call", "page_view", "feature_usage")
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Event category (e.g., "api", "ui", "system")
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Event action (e.g., "GET", "POST", "page_load")
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Event label (e.g., "/api/v1/entries", "dashboard", "glucose_chart")
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Numeric value associated with the event (e.g., response time, count)
    /// </summary>
    public long? Value { get; set; }

    /// <summary>
    /// UTC timestamp when the event occurred
    /// </summary>
    public long Timestamp { get; set; }

    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Batch of analytics events for efficient transmission
/// </summary>
public class AnalyticsBatch
{
    /// <summary>
    /// Anonymous installation identifier (persistent across restarts but not linked to user)
    /// </summary>
    public string InstallationId { get; set; } = string.Empty;

    public List<AnalyticsEvent> Events { get; set; } = new();

    /// <summary>
    /// System information (anonymized)
    /// </summary>
    public SystemInfo? SystemInfo { get; set; }

    /// <summary>
    /// UTC timestamp when the batch was created
    /// </summary>
    public long BatchTimestamp { get; set; }

    public string SchemaVersion { get; set; } = "1.0";
}

/// <summary>
/// Anonymous system information for aggregate usage analysis
/// </summary>
public class SystemInfo
{
    /// <summary>
    /// Operating system platform (Windows, Linux, macOS)
    /// </summary>
    public string Platform { get; set; } = string.Empty;

    public string RuntimeVersion { get; set; } = string.Empty;

    public string NocturneVersion { get; set; } = string.Empty;

    /// <summary>
    /// Deployment type (Docker, Standalone, Aspire)
    /// </summary>
    public string DeploymentType { get; set; } = string.Empty;

    public bool DemoModeEnabled { get; set; }

    /// <summary>
    /// Enabled connectors (anonymized list)
    /// </summary>
    public List<string> EnabledConnectors { get; set; } = new();

    public List<string> EnabledFeatures { get; set; } = new();

    public string DatabaseType { get; set; } = string.Empty;

    public bool CacheEnabled { get; set; }
}

/// <summary>
/// Performance metrics for system health monitoring
/// </summary>
public class PerformanceMetrics
{
    /// <summary>
    /// Average API response time in milliseconds
    /// </summary>
    public double AverageResponseTime { get; set; }

    /// <summary>
    /// Total number of API requests in time period
    /// </summary>
    public long TotalRequests { get; set; }

    /// <summary>
    /// Number of errors in time period
    /// </summary>
    public long ErrorCount { get; set; }

    /// <summary>
    /// Memory usage in MB
    /// </summary>
    public long MemoryUsageMB { get; set; }

    /// <summary>
    /// Uptime in hours
    /// </summary>
    public double UptimeHours { get; set; }

    /// <summary>
    /// Most frequently accessed endpoints (anonymized)
    /// </summary>
    public Dictionary<string, long> TopEndpoints { get; set; } = new();
}

/// <summary>
/// Usage statistics for understanding feature adoption
/// </summary>
public class UsageStatistics
{
    /// <summary>
    /// Number of unique sessions in time period
    /// </summary>
    public long UniqueSessions { get; set; }

    /// <summary>
    /// Most popular pages/features
    /// </summary>
    public Dictionary<string, long> PopularFeatures { get; set; } = new();

    /// <summary>
    /// Average session duration in minutes
    /// </summary>
    public double AverageSessionDuration { get; set; }

    /// <summary>
    /// Device types accessing the system
    /// </summary>
    public Dictionary<string, long> DeviceTypes { get; set; } = new();
}

/// <summary>
/// Configuration for what types of analytics to collect
/// </summary>
public class AnalyticsCollectionConfig
{
    public bool CollectApiUsage { get; set; } = true;

    public bool CollectUiUsage { get; set; } = true;

    public bool CollectPerformanceMetrics { get; set; } = true;

    public bool CollectHealthMetrics { get; set; } = true;

    public bool CollectFeatureUsage { get; set; } = true;

    /// <summary>
    /// Endpoints to exclude from analytics (e.g., health checks)
    /// </summary>
    public List<string> ExcludedEndpoints { get; set; } = new() { "/health", "/metrics", "/ping" };

    /// <summary>
    /// Maximum number of events to store locally before forcing transmission
    /// </summary>
    public int MaxLocalEvents { get; set; } = 1000;
}
