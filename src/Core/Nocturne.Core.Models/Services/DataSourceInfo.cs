using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Services;

/// <summary>
/// Represents information about a data source that's been pushing data to Nocturne.
/// This is derived from analyzing the `device` field on entries and devicestatus records.
/// </summary>
public class DataSourceInfo
{
    /// <summary>
    /// Unique identifier for this data source (derived from device string or generated)
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display name for the data source
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The raw device identifier string from entries
    /// </summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Category of data source: cgm, pump, uploader, aid-system, connector, manual, unknown
    /// </summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = "unknown";

    /// <summary>
    /// More specific type of data source (e.g., "xdrip", "dexcom-share", "loop", "aaps")
    /// </summary>
    [JsonPropertyName("sourceType")]
    public string SourceType { get; set; } = string.Empty;

    /// <summary>
    /// Description of this data source
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp of the most recent data received from this source
    /// </summary>
    [JsonPropertyName("lastSeen")]
    public DateTimeOffset? LastSeen { get; set; }

    /// <summary>
    /// Timestamp of the first data received from this source
    /// </summary>
    [JsonPropertyName("firstSeen")]
    public DateTimeOffset? FirstSeen { get; set; }

    /// <summary>
    /// Number of entries received from this source in the last 24 hours
    /// </summary>
    [JsonPropertyName("entriesLast24h")]
    public int EntriesLast24Hours { get; set; }

    /// <summary>
    /// Total number of entries from this source
    /// </summary>
    [JsonPropertyName("totalEntries")]
    public long TotalEntries { get; set; }

    /// <summary>
    /// Health status based on recent activity: "active", "stale", "inactive"
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    /// <summary>
    /// Minutes since last data was received
    /// </summary>
    [JsonPropertyName("minutesSinceLastData")]
    public int? MinutesSinceLastData { get; set; }

    /// <summary>
    /// Icon identifier for UI display
    /// </summary>
    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;

    /// <summary>
    /// Whether this source is currently considered healthy (received data recently)
    /// </summary>
    [JsonPropertyName("isHealthy")]
    public bool IsHealthy =>
        Status == "active" || (MinutesSinceLastData.HasValue && MinutesSinceLastData.Value < 15);
}

/// <summary>
/// Represents an available connector that can be configured to pull data into Nocturne
/// </summary>
public class AvailableConnector
{
    /// <summary>
    /// Unique identifier for the connector
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display name
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Category: cgm, pump, data, food
    /// </summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Description of what this connector does
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Icon identifier
    /// </summary>
    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;

    /// <summary>
    /// Whether this connector is currently available/enabled on this server
    /// </summary>
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    /// <summary>
    /// Whether this connector requires server-side configuration (vs just pushing via API)
    /// </summary>
    [JsonPropertyName("requiresServerConfig")]
    public bool RequiresServerConfig { get; set; }

    /// <summary>
    /// Whether this connector is currently configured in appsettings
    /// </summary>
    [JsonPropertyName("isConfigured")]
    public bool IsConfigured { get; set; }

    /// <summary>
    /// Configuration fields required for this connector
    /// </summary>
    [JsonPropertyName("configFields")]
    public List<ConnectorConfigField>? ConfigFields { get; set; }

    /// <summary>
    /// URL for documentation/setup instructions
    /// </summary>
    [JsonPropertyName("documentationUrl")]
    public string? DocumentationUrl { get; set; }

    /// <summary>
    /// The data source ID used in the database Device field when this connector writes entries.
    /// Used to match connector data in activeDataSources.
    /// </summary>
    [JsonPropertyName("dataSourceId")]
    public string? DataSourceId { get; set; }
}

/// <summary>
/// Describes a configuration field for a connector
/// </summary>
public class ConnectorConfigField
{
    /// <summary>
    /// Field identifier
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display label
    /// </summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Field type: text, password, select, number
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    /// <summary>
    /// Whether the field is required
    /// </summary>
    [JsonPropertyName("required")]
    public bool Required { get; set; }

    /// <summary>
    /// Placeholder text
    /// </summary>
    [JsonPropertyName("placeholder")]
    public string? Placeholder { get; set; }

    /// <summary>
    /// Help text
    /// </summary>
    [JsonPropertyName("helpText")]
    public string? HelpText { get; set; }

    /// <summary>
    /// Options for select fields
    /// </summary>
    [JsonPropertyName("options")]
    public List<SelectOption>? Options { get; set; }
}

/// <summary>
/// Option for a select field
/// </summary>
public class SelectOption
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// Represents an uploader application that can push data to Nocturne
/// </summary>
public class UploaderApp
{
    /// <summary>
    /// Unique identifier
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display name
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Platform: android, ios, desktop, web
    /// </summary>
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    /// <summary>
    /// Short description
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Category: cgm, pump, aid-system, uploader
    /// </summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Icon identifier
    /// </summary>
    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;

    /// <summary>
    /// Setup instructions
    /// </summary>
    [JsonPropertyName("setupInstructions")]
    public List<SetupStep>? SetupInstructions { get; set; }

    /// <summary>
    /// URL for app download or more info
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>
/// A step in setup instructions
/// </summary>
public class SetupStep
{
    [JsonPropertyName("step")]
    public int Step { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }
}

/// <summary>
/// Complete services overview response
/// </summary>
public class ServicesOverview
{
    /// <summary>
    /// Data sources currently pushing data to this Nocturne instance
    /// </summary>
    [JsonPropertyName("activeDataSources")]
    public List<DataSourceInfo> ActiveDataSources { get; set; } = new();

    /// <summary>
    /// Available connectors that can be configured
    /// </summary>
    [JsonPropertyName("availableConnectors")]
    public List<AvailableConnector> AvailableConnectors { get; set; } = new();

    /// <summary>
    /// Uploader apps that can push data to Nocturne
    /// </summary>
    [JsonPropertyName("uploaderApps")]
    public List<UploaderApp> UploaderApps { get; set; } = new();

    /// <summary>
    /// API endpoint information for configuring uploaders
    /// </summary>
    [JsonPropertyName("apiEndpoint")]
    public ApiEndpointInfo ApiEndpoint { get; set; } = new();
}

/// <summary>
/// Information about the API endpoint for client configuration
/// </summary>
public class ApiEndpointInfo
{
    /// <summary>
    /// Base URL for this Nocturne instance
    /// </summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Whether an API secret is required
    /// </summary>
    [JsonPropertyName("requiresApiSecret")]
    public bool RequiresApiSecret { get; set; } = true;

    /// <summary>
    /// Whether the current request is authenticated
    /// </summary>
    [JsonPropertyName("isAuthenticated")]
    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// Entries API endpoint
    /// </summary>
    [JsonPropertyName("entriesEndpoint")]
    public string EntriesEndpoint { get; set; } = "/api/v1/entries";

    /// <summary>
    /// Treatments API endpoint
    /// </summary>
    [JsonPropertyName("treatmentsEndpoint")]
    public string TreatmentsEndpoint { get; set; } = "/api/v1/treatments";

    /// <summary>
    /// Device status API endpoint
    /// </summary>
    [JsonPropertyName("deviceStatusEndpoint")]
    public string DeviceStatusEndpoint { get; set; } = "/api/v1/devicestatus";
}

/// <summary>
/// Summary of data counts for a connector
/// </summary>
public class ConnectorDataSummary
{
    /// <summary>
    /// The connector ID
    /// </summary>
    [JsonPropertyName("connectorId")]
    public string ConnectorId { get; set; } = string.Empty;

    /// <summary>
    /// Number of glucose entries
    /// </summary>
    [JsonPropertyName("entries")]
    public long Entries { get; set; }

    /// <summary>
    /// Number of treatments
    /// </summary>
    [JsonPropertyName("treatments")]
    public long Treatments { get; set; }

    /// <summary>
    /// Number of device status records
    /// </summary>
    [JsonPropertyName("deviceStatuses")]
    public long DeviceStatuses { get; set; }

    /// <summary>
    /// Total count of all records
    /// </summary>
    [JsonPropertyName("total")]
    public long Total => Entries + Treatments + DeviceStatuses;
}

/// <summary>
/// Result of a data source deletion operation
/// </summary>
public class DataSourceDeleteResult
{
    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Number of entries deleted
    /// </summary>
    [JsonPropertyName("entriesDeleted")]
    public long EntriesDeleted { get; set; }

    /// <summary>
    /// Number of treatments deleted
    /// </summary>
    [JsonPropertyName("treatmentsDeleted")]
    public long TreatmentsDeleted { get; set; }

    /// <summary>
    /// Number of device status records deleted
    /// </summary>
    [JsonPropertyName("deviceStatusDeleted")]
    public long DeviceStatusDeleted { get; set; }

    /// <summary>
    /// The data source that was deleted
    /// </summary>
    [JsonPropertyName("dataSource")]
    public string DataSource { get; set; } = string.Empty;

    /// <summary>
    /// Error message if the operation failed
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
