namespace Nocturne.Core.Models;

/// <summary>
/// V3 Status response model that maintains 1:1 compatibility with Nightscout /api/v3/status endpoint.
/// Includes extended status information with permissions and authorization details.
/// </summary>
/// <seealso cref="StatusResponse"/>
/// <seealso cref="ExtendedStatusInfo"/>
/// <seealso cref="StorageInfo"/>
public class V3StatusResponse
{
    /// <summary>
    /// Server status - typically "ok"
    /// </summary>
    public string Status { get; set; } = "ok";

    /// <summary>
    /// Server name/title
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Current server time in ISO 8601 format
    /// </summary>
    public DateTime ServerTime { get; set; }

    /// <summary>
    /// Server date as Unix epoch milliseconds (legacy: srvDate)
    /// </summary>
    public long SrvDate { get; set; }

    /// <summary>
    /// Storage backend information (legacy: { storage: "mongodb", version: "x.y" })
    /// </summary>
    public StorageInfo Storage { get; set; } = new();

    /// <summary>
    /// V3 API level, <see cref="Nocturne.Core.Constants.ApplicationConstants.NightscoutApi.V3Version"/>
    /// </summary>
    public string ApiVersion { get; set; } = string.Empty;

    /// <summary>
    /// Per-collection CRUD permission strings (legacy: apiPermissions)
    /// </summary>
    public Dictionary<string, string> ApiPermissions { get; set; } = new();

    public bool ApiEnabled { get; set; } = true;

    /// <summary>
    /// Care Portal enabled status
    /// </summary>
    public bool CareportalEnabled { get; set; } = true;

    public string Head { get; set; } = string.Empty;

    /// <summary>
    /// Public settings that can be shared with clients
    /// </summary>
    public Dictionary<string, object> Settings { get; set; } = new();

    /// <summary>
    /// Extended V3 status properties
    /// </summary>
    public ExtendedStatusInfo Extended { get; set; } = new();
}

public class StorageInfo
{
    /// <summary>
    /// Storage type (e.g. "mongodb", "postgresql")
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("storage")]
    public string StorageType { get; set; } = "postgresql";

    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// Extended status information for V3 API
/// </summary>
public class ExtendedStatusInfo
{
    public AuthorizationInfo Authorization { get; set; } = new();

    /// <summary>
    /// API permissions and capabilities
    /// </summary>
    public Dictionary<string, bool> Permissions { get; set; } = new();

    /// <summary>
    /// System uptime in milliseconds
    /// </summary>
    public long UptimeMs { get; set; }

    /// <summary>
    /// Available API collections
    /// </summary>
    public List<string> Collections { get; set; } = new();

    /// <summary>
    /// API version support matrix
    /// </summary>
    public Dictionary<string, bool> ApiVersions { get; set; } = new();
}

public class AuthorizationInfo
{
    public bool IsAuthorized { get; set; }

    public List<string> Scope { get; set; } = new();

    /// <summary>
    /// Subject identifier (if authenticated)
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Roles assigned to the current session
    /// </summary>
    public List<string> Roles { get; set; } = new();
}
