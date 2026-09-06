using System.ComponentModel.DataAnnotations;

namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// Root configuration class covering all appsettings sections.
/// Provides strongly-typed access to all Nocturne configuration.
/// </summary>
public class NocturneOptions
{
    /// <summary>
    /// PostgreSQL database configuration.
    /// </summary>
    public PostgreSqlOptions PostgreSql { get; set; } = new();

    /// <summary>
    /// JWT authentication configuration.
    /// </summary>
    public JwtOptions Jwt { get; set; } = new();

    /// <summary>
    /// API settings including pagination, rate limiting, and CORS.
    /// </summary>
    public ApiSettingsOptions ApiSettings { get; set; } = new();

    /// <summary>
    /// Health check configuration.
    /// </summary>
    public HealthChecksOptions HealthChecks { get; set; } = new();

    /// <summary>
    /// OpenTelemetry observability configuration.
    /// </summary>
    public OpenTelemetryOptions OpenTelemetry { get; set; } = new();

    /// <summary>
    /// Logging configuration.
    /// </summary>
    public LoggingOptions Logging { get; set; } = new();

    /// <summary>
    /// Parameters section containing secrets, demo mode, connectors, etc.
    /// </summary>
    public ParametersOptions Parameters { get; set; } = new();
}

/// <summary>
/// PostgreSQL database configuration options.
/// </summary>
public class PostgreSqlOptions
{
    public const string SectionName = "PostgreSql";

    /// <summary>
    /// If true, uses ConnectionStrings.nocturne-postgres; if false, Aspire manages local container.
    /// </summary>
    public bool UseRemoteDatabase { get; set; }

    /// <summary>
    /// Enable sensitive data logging in EF Core (passwords, etc.). Only for debugging.
    /// </summary>
    public bool EnableSensitiveDataLogging { get; set; }

    /// <summary>
    /// Enable detailed EF Core errors.
    /// </summary>
    public bool EnableDetailedErrors { get; set; }

    /// <summary>
    /// Maximum number of retry attempts for transient failures.
    /// </summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>
    /// Maximum delay in seconds between retries.
    /// </summary>
    public int MaxRetryDelaySeconds { get; set; } = 30;

    /// <summary>
    /// Command timeout in seconds for database operations.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// JWT authentication configuration options.
/// </summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// Secret key for signing JWT tokens. Derived automatically from instance key
    /// via PostConfigure if not explicitly set.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// JWT token issuer.
    /// </summary>
    public string Issuer { get; set; } = "nocturne";

    /// <summary>
    /// JWT token audience.
    /// </summary>
    public string Audience { get; set; } = "nocturne-api";

    /// <summary>
    /// Access token lifetime in minutes.
    /// </summary>
    public int AccessTokenLifetimeMinutes { get; set; } = 15;

    /// <summary>
    /// Refresh token lifetime in days.
    /// </summary>
    public int RefreshTokenLifetimeDays { get; set; } = 7;

    /// <summary>
    /// Refresh token length in bytes (will be base64 encoded).
    /// </summary>
    public int RefreshTokenLengthBytes { get; set; } = 64;
}

/// <summary>
/// API settings configuration options.
/// </summary>
public class ApiSettingsOptions
{
    public const string SectionName = "ApiSettings";

    /// <summary>
    /// Default page size for paginated results.
    /// </summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>
    /// Enable Swagger/OpenAPI documentation.
    /// </summary>
    public bool EnableSwagger { get; set; } = true;

    /// <summary>
    /// Enable CORS support.
    /// </summary>
    public bool EnableCors { get; set; } = true;

    /// <summary>
    /// Rate limiting configuration.
    /// </summary>
    public RateLimitingOptions RateLimiting { get; set; } = new();
}

/// <summary>
/// Rate limiting configuration options.
/// </summary>
public class RateLimitingOptions
{
    /// <summary>
    /// Enable rate limiting.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum requests per minute.
    /// </summary>
    public int RequestsPerMinute { get; set; } = 100;
}

/// <summary>
/// Health check configuration options.
/// </summary>
public class HealthChecksOptions
{
    public const string SectionName = "HealthChecks";

    /// <summary>
    /// Enable health check endpoints.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Include detailed error information in health check responses.
    /// </summary>
    public bool DetailedErrors { get; set; }

    /// <summary>
    /// Individual health check configurations.
    /// </summary>
    public HealthCheckOptions Checks { get; set; } = new();
}

/// <summary>
/// Individual health check configurations.
/// </summary>
public class HealthCheckOptions
{
    /// <summary>
    /// Database health check configuration.
    /// </summary>
    public DatabaseHealthCheckOptions Database { get; set; } = new();
}

/// <summary>
/// Database health check configuration.
/// </summary>
public class DatabaseHealthCheckOptions
{
    /// <summary>
    /// Enable database health check.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Health check timeout as TimeSpan string (e.g., "00:00:30").
    /// </summary>
    public string Timeout { get; set; } = "00:00:30";
}

/// <summary>
/// OpenTelemetry observability configuration options.
/// </summary>
public class OpenTelemetryOptions
{
    public const string SectionName = "OpenTelemetry";

    /// <summary>
    /// Service name for telemetry.
    /// </summary>
    public string ServiceName { get; set; } = "Nocturne";

    /// <summary>
    /// Service version for telemetry.
    /// </summary>
    public string ServiceVersion { get; set; } = "1.0.0";

    /// <summary>
    /// Tracing configuration.
    /// </summary>
    public TracingOptions Tracing { get; set; } = new();

    /// <summary>
    /// Metrics configuration.
    /// </summary>
    public MetricsOptions Metrics { get; set; } = new();
}

/// <summary>
/// Tracing configuration options.
/// </summary>
public class TracingOptions
{
    /// <summary>
    /// Enable distributed tracing.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Trace exporters to use (e.g., "console", "otlp").
    /// </summary>
    public List<string> Exporters { get; set; } = new() { "console", "otlp" };

    /// <summary>
    /// OTLP endpoint for trace export.
    /// </summary>
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";
}

/// <summary>
/// Metrics configuration options.
/// </summary>
public class MetricsOptions
{
    /// <summary>
    /// Enable metrics collection.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Metrics exporters to use (e.g., "console", "otlp").
    /// </summary>
    public List<string> Exporters { get; set; } = new() { "console", "otlp" };
}

/// <summary>
/// Logging configuration options.
/// </summary>
public class LoggingOptions
{
    public const string SectionName = "Logging";

    /// <summary>
    /// Log level configuration by category.
    /// </summary>
    public Dictionary<string, string> LogLevel { get; set; } = new()
    {
        ["Default"] = "Information",
        ["Microsoft.AspNetCore"] = "Warning"
    };

    /// <summary>
    /// Console logging configuration.
    /// </summary>
    public ConsoleLoggingOptions Console { get; set; } = new();
}

/// <summary>
/// Console logging configuration options.
/// </summary>
public class ConsoleLoggingOptions
{
    /// <summary>
    /// Include scopes in console output.
    /// </summary>
    public bool IncludeScopes { get; set; }

    /// <summary>
    /// Console-specific log levels.
    /// </summary>
    public Dictionary<string, string> LogLevel { get; set; } = new()
    {
        ["Default"] = "Information"
    };
}

/// <summary>
/// Parameters section configuration containing secrets, demo mode, connectors, etc.
/// </summary>
public class ParametersOptions
{
    public const string SectionName = "Parameters";

    /// <summary>
    /// Instance key for JWT signing and encryption.
    /// </summary>
    public string InstanceKey { get; set; } = string.Empty;

    /// <summary>
    /// SignalR hub URL for real-time communication.
    /// </summary>
    public string SignalrHubUrl { get; set; } = string.Empty;

    /// <summary>
    /// Demo mode configuration.
    /// </summary>
    public DemoModeOptions DemoMode { get; set; } = new();

    /// <summary>
    /// Connector configurations.
    /// </summary>
    public ConnectorsOptions Connectors { get; set; } = new();

    /// <summary>
    /// Compatibility proxy configuration.
    /// </summary>
    public CompatibilityProxyOptions CompatibilityProxy { get; set; } = new();
}

/// <summary>
/// Demo mode configuration options.
/// </summary>
public class DemoModeOptions
{
    /// <summary>
    /// Enable demo mode with generated glucose data.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Clear existing data on startup when demo mode is enabled.
    /// </summary>
    public bool ClearOnStartup { get; set; } = true;

    /// <summary>
    /// Regenerate demo data on startup.
    /// </summary>
    public bool RegenerateOnStartup { get; set; } = true;

    /// <summary>
    /// Number of days to backfill with demo data.
    /// </summary>
    public int BackfillDays { get; set; } = 90;

    /// <summary>
    /// Interval in minutes between generated readings.
    /// </summary>
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Target glucose level for demo data generation.
    /// </summary>
    public double TargetGlucose { get; set; } = 110.0;

    /// <summary>
    /// Insulin sensitivity factor for demo data.
    /// </summary>
    public double InsulinSensitivityFactor { get; set; } = 40.0;

    /// <summary>
    /// Carb ratio for demo data.
    /// </summary>
    public double CarbRatio { get; set; } = 10.0;

    /// <summary>
    /// Basal rate for demo data.
    /// </summary>
    public double BasalRate { get; set; } = 1.0;

    /// <summary>
    /// Insulin duration in minutes.
    /// </summary>
    public double InsulinDurationMinutes { get; set; } = 180.0;

    /// <summary>
    /// Insulin peak time in minutes.
    /// </summary>
    public double InsulinPeakMinutes { get; set; } = 75.0;

    /// <summary>
    /// Temp basal duration in minutes.
    /// </summary>
    public int TempBasalDurationMinutes { get; set; } = 10;

    /// <summary>
    /// Minimum glucose value for demo data.
    /// </summary>
    public int MinGlucose { get; set; } = 40;

    /// <summary>
    /// Maximum glucose value for demo data.
    /// </summary>
    public int MaxGlucose { get; set; } = 300;

    /// <summary>
    /// Device identifier for demo data.
    /// </summary>
    public string Device { get; set; } = "demo-cgm";
}

/// <summary>
/// Connectors section configuration.
/// </summary>
public class ConnectorsOptions
{
    /// <summary>
    /// Global connector settings.
    /// </summary>
    public ConnectorSettingsOptions Settings { get; set; } = new();
}

/// <summary>
/// Global connector settings.
/// </summary>
public class ConnectorSettingsOptions
{
    /// <summary>
    /// Default number of days to backfill for connectors.
    /// </summary>
    public int BackfillDays { get; set; } = 90;

    /// <summary>
    /// Global enable flag for connectors.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Compatibility proxy configuration for fallback to Nightscout.
/// </summary>
public class CompatibilityProxyOptions
{
    public const string SectionName = "Parameters:CompatibilityProxy";

    /// <summary>
    /// Enable the compatibility proxy.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Nightscout URL to proxy to.
    /// </summary>
    public string NightscoutUrl { get; set; } = string.Empty;

    /// <summary>
    /// Nightscout API secret for authentication.
    /// </summary>
    public string NightscoutApiSecret { get; set; } = string.Empty;

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Number of retry attempts for failed requests.
    /// </summary>
    public int RetryAttempts { get; set; } = 3;

    /// <summary>
    /// Default data fetching strategy.
    /// </summary>
    public string DefaultStrategy { get; set; } = "Nightscout";

    /// <summary>
    /// Enable detailed logging for debugging.
    /// </summary>
    public bool EnableDetailedLogging { get; set; }

    /// <summary>
    /// Comparison configuration for data validation.
    /// </summary>
    public ComparisonOptions Comparison { get; set; } = new();
}

/// <summary>
/// Data comparison options for compatibility proxy.
/// </summary>
public class ComparisonOptions
{
    /// <summary>
    /// Enable deep comparison of data from Nightscout vs Nocturne.
    /// </summary>
    public bool EnableDeepComparison { get; set; } = true;
}
