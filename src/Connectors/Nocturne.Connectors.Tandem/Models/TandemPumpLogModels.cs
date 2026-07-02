using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Tandem.Models;

/// <summary>
/// Response of <c>GET api/reports/bff/pump-logs/{deviceAssignmentId}</c>. Replaces the old base64
/// <c>reportsfacade/pumpevents</c> payload (tconnectsync v3's <c>PumpLogsResponse</c>). Clock
/// changes (LID_TIME_CHANGED / LID_DATE_CHANGED) are returned separately and are not consumed.
/// </summary>
public sealed class TandemPumpLogsResponse
{
    [JsonPropertyName("events")]
    public List<TandemPumpLogEvent> Events { get; set; } = [];

    [JsonPropertyName("clockChanges")]
    public List<TandemPumpLogEvent> ClockChanges { get; set; } = [];
}

/// <summary>
/// One pump-logs event, pre-decoded by the server (tconnectsync v3's <c>PumpLogEvent</c>).
/// <see cref="EventProperties"/> holds the per-event fields keyed by camelCase name with <b>raw</b>
/// values (client-side transforms such as ratio scaling still apply); bitmask fields arrive as
/// arrays of set-bit indices. <see cref="PumpDateTime"/> is the pump's local wall-clock time
/// (ISO-8601, no tz); <see cref="SequenceNumber"/> equals the old binary header's seqNum.
/// </summary>
public sealed class TandemPumpLogEvent
{
    [JsonPropertyName("eventCode")]
    public int EventCode { get; set; }

    [JsonPropertyName("sequenceGroup")]
    public long SequenceGroup { get; set; }

    [JsonPropertyName("sequenceNumber")]
    public uint SequenceNumber { get; set; }

    [JsonPropertyName("pumpDateTime")]
    public string? PumpDateTime { get; set; }

    [JsonPropertyName("eventProperties")]
    public Dictionary<string, JsonElement> EventProperties { get; set; } = [];
}
