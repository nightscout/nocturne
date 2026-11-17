using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Point-in-time system event (alarm, warning, info) from a pump or CGM.
/// System events are stored in the <c>system_events</c> table and are displayed on the chart timeline.
/// </summary>
/// <seealso cref="SystemEventType"/>
/// <seealso cref="SystemEventCategory"/>
public class SystemEvent
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("eventType")]
    public SystemEventType EventType { get; set; }

    [JsonPropertyName("category")]
    public SystemEventCategory Category { get; set; }

    /// <summary>
    /// Device-specific event code
    /// </summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>
    /// Human-readable description
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// When the event occurred (Unix milliseconds)
    /// </summary>
    [JsonPropertyName("mills")]
    public long Mills { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// Additional event details (stored as JSON)
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Original ID from source system for deduplication
    /// </summary>
    [JsonPropertyName("originalId")]
    public string? OriginalId { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }
}
