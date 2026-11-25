using System.Text.Json.Serialization;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Connectors;

namespace Nocturne.Core.Models.Services;

/// <summary>What a connection is made of.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectionKind>))]
public enum ConnectionKind
{
    /// <summary>An app the member authorized, under one or more grants.</summary>
    [JsonStringEnumMemberName("app")] App,

    /// <summary>An API token.</summary>
    [JsonStringEnumMemberName("token")] Token,

    /// <summary>A server connector that pulls from another service.</summary>
    [JsonStringEnumMemberName("connector")] Connector,
}

/// <summary>Which way a connection's records travel.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectionDirection>))]
public enum ConnectionDirection
{
    /// <summary>The connection wrote records inside the window.</summary>
    [JsonStringEnumMemberName("sends")] Sends,

    /// <summary>Nocturne pulls the records from the service.</summary>
    [JsonStringEnumMemberName("fetches")] Fetches,

    /// <summary>The records came from a file the member imported.</summary>
    [JsonStringEnumMemberName("imports")] Imports,

    /// <summary>A credential that wrote no record inside the window.</summary>
    [JsonStringEnumMemberName("noRecords")] NoRecords,
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionStatus>))]
public enum ConnectionStatus
{
    [JsonStringEnumMemberName("active")] Active,

    /// <summary>Nothing recent, and nothing wrong.</summary>
    [JsonStringEnumMemberName("quiet")] Quiet,

    /// <summary>
    /// A connection that sent glucose or device status inside the window and has sent no record
    /// recently: a source of continuous data that went silent.
    /// </summary>
    [JsonStringEnumMemberName("stopped")] Stopped,

    /// <summary>A credential that has not been used yet.</summary>
    [JsonStringEnumMemberName("waiting")] Waiting,

    [JsonStringEnumMemberName("error")] Error,

    /// <summary>A connector with no sync, whose records were imported from a file.</summary>
    [JsonStringEnumMemberName("imported")] Imported,

    /// <summary>A connector that is configured and turned off.</summary>
    [JsonStringEnumMemberName("disabled")] Disabled,

    /// <summary>Every credential of the connection is revoked; only its records remain.</summary>
    [JsonStringEnumMemberName("revoked")] Revoked,
}

/// <summary>
/// The records of one connection that share both origin handles: what one app relays from one
/// device or feed.
/// </summary>
public class ConnectionStream
{
    /// <summary><c>DataSource ?? Device</c>, the identifier a delete of the stream's records takes.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("dataSource")]
    public string? DataSource { get; set; }

    /// <summary>The device string the uploader reported for itself.</summary>
    [JsonPropertyName("device")]
    public string? Device { get; set; }

    /// <summary>The product the origin handles name, or the cleaned identifier when none is recognized.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sourceType")]
    public string SourceType { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;

    /// <summary>
    /// Whether a delete by <see cref="Key"/> removes only this stream's records. False for a
    /// credential's stream, and for a source that shares an origin handle with one: the delete
    /// matches rows by handle, whoever wrote them.
    /// </summary>
    [JsonPropertyName("canDelete")]
    public bool CanDelete { get; set; }

    /// <summary>Records inside the window by data type.</summary>
    [JsonPropertyName("recordCounts")]
    public Dictionary<string, long> RecordCounts { get; set; } = new();

    [JsonPropertyName("totalRecords")]
    public long TotalRecords { get; set; }

    [JsonPropertyName("recordsLast24Hours")]
    public int RecordsLast24Hours { get; set; }

    [JsonPropertyName("lastRecordAt")]
    public DateTimeOffset? LastRecordAt { get; set; }

    [JsonPropertyName("firstRecordAt")]
    public DateTimeOffset? FirstRecordAt { get; set; }
}

/// <summary>One grant a connection authenticates under.</summary>
public class ConnectionCredential
{
    [JsonPropertyName("grantId")]
    public Guid GrantId { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>
    /// The scopes the grant authorizes, as a listing shows them. Another member's grant is listed
    /// with its label alone: no scopes, no use time and no device count.
    /// </summary>
    [JsonPropertyName("scopes")]
    public List<string> Scopes { get; set; } = [];

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("lastUsedAt")]
    public DateTime? LastUsedAt { get; set; }

    [JsonPropertyName("isRevoked")]
    public bool IsRevoked { get; set; }

    /// <summary>Whether the grant may read only the last 24 hours of time-series data.</summary>
    [JsonPropertyName("limitTo24Hours")]
    public bool LimitTo24Hours { get; set; }

    /// <summary>
    /// Whether the credential was taken on from another instance, not minted here: a classic
    /// Nightscout secret or token.
    /// </summary>
    [JsonPropertyName("isMigrated")]
    public bool IsMigrated { get; set; }

    /// <summary>Devices paired under the grant, which a revoke removes with it.</summary>
    [JsonPropertyName("deviceCount")]
    public int DeviceCount { get; set; }

    /// <summary>Whether the grant belongs to the caller, who alone can revoke it here.</summary>
    [JsonPropertyName("isOwn")]
    public bool IsOwn { get; set; }

    /// <summary>
    /// When a device credential a member set up stops authenticating: the end of that member's
    /// access, or earlier. Null for any other credential and for a device credential that does not stop.
    /// </summary>
    [JsonPropertyName("stopsAt")]
    public DateTime? StopsAt { get; set; }

    /// <summary>Whether <see cref="StopsAt"/> has passed.</summary>
    [JsonPropertyName("hasStopped")]
    public bool HasStopped { get; set; }

    /// <summary>The name of the member who set up a credential that stops, when it is known.</summary>
    [JsonPropertyName("setUpByName")]
    public string? SetUpByName { get; set; }

    /// <summary>
    /// Whether the viewer may remove <see cref="StopsAt"/>, so the credential does not stop: only a
    /// viewer whose own access does not end and who manages the site's members.
    /// </summary>
    [JsonPropertyName("canKeep")]
    public bool CanKeep { get; set; }
}

/// <summary>
/// One thing a member set up that writes to or reads from the tenant: an authorized app, an API
/// token, or a server connector.
/// </summary>
public class Connection
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public ConnectionKind Kind { get; set; }

    [JsonPropertyName("direction")]
    public ConnectionDirection Direction { get; set; }

    [JsonPropertyName("status")]
    public ConnectionStatus Status { get; set; }

    /// <summary>The app's or connector's name, or a token's label. Null for a token with no label.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("logoUri")]
    public string? LogoUri { get; set; }

    [JsonPropertyName("clientUri")]
    public string? ClientUri { get; set; }

    /// <summary>How far the app's identity is verified, for an app.</summary>
    [JsonPropertyName("identity")]
    public OAuthClientIdentity? Identity { get; set; }

    /// <summary>The connector's identifier, for a connector.</summary>
    [JsonPropertyName("connectorId")]
    public string? ConnectorId { get; set; }

    /// <summary>The connector's last error, for a connection whose status is an error.</summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("lastRecordAt")]
    public DateTimeOffset? LastRecordAt { get; set; }

    /// <summary>The zone a connector read its source's timestamps in on its last sync, when it records one.</summary>
    [JsonPropertyName("timeZone")]
    public ConnectorTimeZone? TimeZone { get; set; }

    /// <summary>When the connector last completed a sync.</summary>
    [JsonPropertyName("lastSyncAt")]
    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>When any credential of the connection was last presented.</summary>
    [JsonPropertyName("lastUsedAt")]
    public DateTimeOffset? LastUsedAt { get; set; }

    [JsonPropertyName("totalRecords")]
    public long TotalRecords { get; set; }

    [JsonPropertyName("recordsLast24Hours")]
    public int RecordsLast24Hours { get; set; }

    /// <summary>Records inside the window by data type, across the connection's streams.</summary>
    [JsonPropertyName("recordCounts")]
    public Dictionary<string, long> RecordCounts { get; set; } = new();

    [JsonPropertyName("streams")]
    public List<ConnectionStream> Streams { get; set; } = [];

    /// <summary>Newest first. Empty for a connector.</summary>
    [JsonPropertyName("credentials")]
    public List<ConnectionCredential> Credentials { get; set; } = [];

    /// <summary>
    /// Whether the viewer may delete every record the connection wrote: true only when each credential
    /// folded into it, listed or not and revoked or not, is the viewer's. False for a connector, whose
    /// records go by <see cref="ConnectionStream.CanDelete"/>.
    /// </summary>
    [JsonPropertyName("canDeleteRecords")]
    public bool CanDeleteRecords { get; set; }
}

public class ConnectionsOverview
{
    /// <summary>
    /// Connections that are active, stopped, waiting or in error first, those with records ahead of
    /// those without, then by their latest record, sync or use.
    /// </summary>
    [JsonPropertyName("connections")]
    public List<Connection> Connections { get; set; } = [];

    /// <summary>
    /// Records inside the window that no credential and no connector is recorded as the writer of,
    /// grouped by their origin handles.
    /// </summary>
    [JsonPropertyName("otherSources")]
    public List<ConnectionStream> OtherSources { get; set; } = [];

    /// <summary>
    /// Minutes without a record after which a connection that sends is no longer active.
    /// </summary>
    [JsonPropertyName("activeWithinMinutes")]
    public int ActiveWithinMinutes { get; set; }

    /// <summary>Days back from now that every record count covers.</summary>
    [JsonPropertyName("windowDays")]
    public int WindowDays { get; set; }
}
