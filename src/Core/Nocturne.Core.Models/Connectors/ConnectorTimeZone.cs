using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Connectors;

/// <summary>Where the zone a connector reads its source's timestamps in came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectorTimeZoneSource>))]
public enum ConnectorTimeZoneSource
{
    /// <summary>The connector's own zone setting.</summary>
    [JsonStringEnumMemberName("connectorZone")] ConnectorZone,

    /// <summary>The connector's fixed offset setting.</summary>
    [JsonStringEnumMemberName("connectorOffset")] ConnectorOffset,

    /// <summary>The tenant's time zone, because the connector has neither setting.</summary>
    [JsonStringEnumMemberName("tenantZone")] TenantZone,

    /// <summary>
    /// The tenant's time zone history, because the connector has neither setting. The zone is the one
    /// in effect at the end of the run.
    /// </summary>
    [JsonStringEnumMemberName("tenantTimeline")] TenantTimeline,

    /// <summary>No setting and no tenant zone, so no shift.</summary>
    [JsonStringEnumMemberName("utc")] Utc,
}

/// <summary>The zone a connector read its source's timestamps in on its last sync.</summary>
public class ConnectorTimeZone
{
    /// <summary>
    /// The key a connector keeps its <see cref="ConnectorTimeZone"/> under in its vendor state, for the
    /// connection list to read.
    /// </summary>
    public const string VendorStateKey = "timeZone";

    /// <summary>The zone stored under <see cref="VendorStateKey"/>, or null when none is stored or it cannot be read.</summary>
    public static ConnectorTimeZone? FromStored(JsonNode? stored)
    {
        try { return stored?.Deserialize<ConnectorTimeZone>(); }
        catch (JsonException) { return null; }
    }

    /// <summary>An IANA zone id, or <c>UTC+05:30</c> for a fixed offset.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public ConnectorTimeZoneSource Source { get; set; }
}
