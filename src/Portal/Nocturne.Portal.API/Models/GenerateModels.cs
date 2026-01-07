namespace Nocturne.Portal.API.Models;

/// <summary>
/// Request for generating docker-compose files
/// </summary>
public class GenerateRequest
{
    /// <summary>
    /// Setup type: "fresh", "migrate", or "compatibility-proxy"
    /// </summary>
    public string SetupType { get; set; } = "fresh";

    /// <summary>
    /// Migration configuration (required if SetupType is "migrate")
    /// </summary>
    public MigrationConfig? Migration { get; set; }

    /// <summary>
    /// Compatibility proxy configuration (required if SetupType is "compatibility-proxy")
    /// </summary>
    public CompatibilityProxyConfig? CompatibilityProxy { get; set; }

    /// <summary>
    /// PostgreSQL configuration
    /// </summary>
    public PostgresConfig Postgres { get; set; } = new();

    /// <summary>
    /// Optional services configuration
    /// </summary>
    public OptionalServicesConfig OptionalServices { get; set; } = new();

    /// <summary>
    /// Selected connectors with their configuration
    /// </summary>
    public List<ConnectorConfig> Connectors { get; set; } = [];
}

public class MigrationConfig
{
    public string NightscoutUrl { get; set; } = string.Empty;
    public string NightscoutApiSecret { get; set; } = string.Empty;
}

public class CompatibilityProxyConfig
{
    public string NightscoutUrl { get; set; } = string.Empty;
    public string NightscoutApiSecret { get; set; } = string.Empty;
}

public class PostgresConfig
{
    /// <summary>
    /// Whether to use the included PostgreSQL container
    /// </summary>
    public bool UseContainer { get; set; } = true;

    /// <summary>
    /// External connection string (required if UseContainer is false)
    /// </summary>
    public string? ConnectionString { get; set; }
}

public class OptionalServicesConfig
{
    /// <summary>
    /// Enable Watchtower for auto-updates
    /// </summary>
    public bool Watchtower { get; set; }
}

public class ConnectorConfig
{
    /// <summary>
    /// Connector type (e.g., "Dexcom")
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Configuration values as key-value pairs
    /// </summary>
    public Dictionary<string, string> Config { get; set; } = [];
}
