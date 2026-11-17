namespace Nocturne.API.Configuration;

/// <summary>
/// Configuration for the compatibility proxy service target endpoints
/// </summary>
public class CompatibilityProxyConfiguration
{
    /// <summary>
    /// Configuration section name for compatibility proxy settings
    /// </summary>
    public const string ConfigurationSection = "Parameters:CompatibilityProxy";

    /// <summary>
    /// Whether the compatibility proxy is enabled
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Request timeout in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Number of retry attempts
    /// </summary>
    public int RetryAttempts { get; set; } = 3;

    /// <summary>
    /// Enable detailed request/response logging
    /// </summary>
    public bool EnableDetailedLogging { get; set; } = false;

    /// <summary>
    /// Response comparison settings
    /// </summary>
    public ResponseComparisonSettings Comparison { get; set; } = new();

    /// <summary>
    /// Circuit breaker settings
    /// </summary>
    public CircuitBreakerSettings CircuitBreaker { get; set; } = new();

    /// <summary>
    /// Enable request correlation tracking
    /// </summary>
    public bool EnableCorrelationTracking { get; set; } = true;

    /// <summary>
    /// Redaction settings for sensitive data handling
    /// </summary>
    public RedactionSettings Redaction { get; set; } = new();

    /// <summary>
    /// Discrepancy forwarding configuration for remote monitoring
    /// </summary>
    public DiscrepancyForwardingSettings DiscrepancyForwarding { get; set; } = new();
}

/// <summary>
/// Response comparison configuration
/// </summary>
public class ResponseComparisonSettings
{
    /// <summary>
    /// Fields to exclude from comparison globally
    /// </summary>
    public List<string> ExcludeFields { get; set; } = new() { "timestamp", "date", "dateString", "_id", "id", "sysTime", "mills", "created_at", "updated_at" };

    /// <summary>
    /// Per-route field exclusions (route pattern -> list of fields to exclude)
    /// </summary>
    public Dictionary<string, List<string>> RouteExcludeFields { get; set; } = new()
    {
        // Example: "/api/v1/entries" excludes additional fields specific to entries
        { "/api/v1/entries", new() { "sgv", "trend" } },
        { "/api/v1/treatments", new() { "insulin", "carbs" } },
    };

    /// <summary>
    /// Allow superset responses (Nocturne can have extra fields that Nightscout doesn't)
    /// </summary>
    public bool AllowSupersetResponses { get; set; } = true;

    /// <summary>
    /// Tolerance for timestamp differences in milliseconds
    /// </summary>
    public long TimestampToleranceMs { get; set; } = 5000; // 5 seconds

    /// <summary>
    /// Tolerance for numeric precision differences
    /// </summary>
    public double NumericPrecisionTolerance { get; set; } = 0.001;

    /// <summary>
    /// Whether to normalize field ordering
    /// </summary>
    public bool NormalizeFieldOrdering { get; set; } = true;

    /// <summary>
    /// How to handle array order differences
    /// </summary>
    public ArrayOrderHandling ArrayOrderHandling { get; set; } = ArrayOrderHandling.Strict;

    /// <summary>
    /// Enable deep comparison for nested objects
    /// </summary>
    public bool EnableDeepComparison { get; set; } = true;
}

/// <summary>
/// Circuit breaker configuration
/// </summary>
public class CircuitBreakerSettings
{
    /// <summary>
    /// Number of consecutive failures before opening circuit
    /// </summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>
    /// Time to wait before attempting to close circuit (in seconds)
    /// </summary>
    public int RecoveryTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Number of successful requests needed to close circuit
    /// </summary>
    public int SuccessThreshold { get; set; } = 3;
}

/// <summary>
/// Configuration for forwarding discrepancies to a remote endpoint
/// </summary>
public class DiscrepancyForwardingSettings
{
    /// <summary>
    /// Enable discrepancy forwarding to remote endpoint
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Save discrepancy data to JSON files for local logging.
    /// Works even without a remote endpoint configured.
    /// </summary>
    public bool SaveRawData { get; set; } = false;

    /// <summary>
    /// Directory for saving raw discrepancy data files.
    /// Defaults to "discrepancies" folder in the application root.
    /// </summary>
    public string DataDirectory { get; set; } = "discrepancies";

    /// <summary>
    /// Remote Nocturne endpoint URL (e.g., https://monitor.nocturne.example.com)
    /// </summary>
    public string EndpointUrl { get; set; } = string.Empty;

    /// <summary>
    /// API key for authenticating with the remote endpoint
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Source identifier for this Nocturne instance
    /// </summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>
    /// Minimum severity level to forward (Minor, Major, Critical)
    /// </summary>
    public DiscrepancySeverityLevel MinimumSeverity { get; set; } = DiscrepancySeverityLevel.Minor;

    /// <summary>
    /// Timeout for forwarding requests in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum retry attempts for failed forwards
    /// </summary>
    public int RetryAttempts { get; set; } = 3;

    /// <summary>
    /// Delay between retry attempts in milliseconds
    /// </summary>
    public int RetryDelayMs { get; set; } = 1000;
}

/// <summary>
/// Severity levels for discrepancy filtering
/// </summary>
public enum DiscrepancySeverityLevel
{
    /// <summary>
    /// Forward all discrepancies including minor ones
    /// </summary>
    Minor = 0,

    /// <summary>
    /// Forward major and critical discrepancies only
    /// </summary>
    Major = 1,

    /// <summary>
    /// Forward only critical discrepancies
    /// </summary>
    Critical = 2,
}

/// <summary>
/// Configuration for sensitive data redaction
/// </summary>
public class RedactionSettings
{
    /// <summary>
    /// Mandatory fields that are ALWAYS redacted regardless of configuration.
    /// These cannot be disabled for security reasons.
    /// </summary>
    private static readonly HashSet<string> MandatorySensitiveFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "api_secret",
        "token",
        "password",
        "key",
        "secret",
        "authorization"
    };

    /// <summary>
    /// Enable redaction of sensitive data in error messages and logs.
    /// Note: Mandatory fields are always redacted even if this is disabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Additional fields to redact from error messages and logs (beyond mandatory fields)
    /// </summary>
    public List<string> SensitiveFields { get; set; } = new();

    /// <summary>
    /// Replacement text for redacted values
    /// </summary>
    public string ReplacementText { get; set; } = "[REDACTED]";

    /// <summary>
    /// Gets all sensitive fields including mandatory ones
    /// </summary>
    public IEnumerable<string> GetAllSensitiveFields()
    {
        return MandatorySensitiveFields.Union(SensitiveFields, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// How to handle array order differences during comparison
/// </summary>
public enum ArrayOrderHandling
{
    /// <summary>
    /// Arrays must have identical ordering
    /// </summary>
    Strict,

    /// <summary>
    /// Arrays can have different ordering but same elements
    /// </summary>
    Loose,

    /// <summary>
    /// Sort arrays before comparison
    /// </summary>
    Sorted,
}
