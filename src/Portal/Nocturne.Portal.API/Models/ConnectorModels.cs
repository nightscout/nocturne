namespace Nocturne.Portal.API.Models;

/// <summary>
/// Connector metadata for the configuration wizard
/// </summary>
public class ConnectorMetadataDto
{
    /// <summary>
    /// Connector type identifier (e.g., "Dexcom")
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Category for grouping (e.g., "Cgm", "Pump")
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Icon identifier for the UI
    /// </summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>
    /// Configuration fields for the wizard form
    /// </summary>
    public List<ConnectorFieldDto> Fields { get; set; } = [];
}

/// <summary>
/// A configuration field for a connector
/// </summary>
public class ConnectorFieldDto
{
    /// <summary>
    /// Field name (property name)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Environment variable name
    /// </summary>
    public string EnvVar { get; set; } = string.Empty;

    /// <summary>
    /// Field type: "string", "password", "boolean", "select"
    /// </summary>
    public string Type { get; set; } = "string";

    /// <summary>
    /// Whether this field is required
    /// </summary>
    public bool Required { get; set; }

    /// <summary>
    /// Human-readable description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Default value if not specified
    /// </summary>
    public string? Default { get; set; }

    /// <summary>
    /// Options for select-type fields
    /// </summary>
    public List<string>? Options { get; set; }
}

/// <summary>
/// Response from GET /api/connectors
/// </summary>
public class ConnectorsResponse
{
    public List<ConnectorMetadataDto> Connectors { get; set; } = [];
}
