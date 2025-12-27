using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a point-in-time system event (alarm, warning, info)
/// </summary>
public class SystemEvent
{
    /// <summary>
    /// Gets or sets the unique identifier
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the event severity/type
    /// </summary>
    [JsonPropertyName("eventType")]
    public SystemEventType EventType { get; set; }

    /// <summary>
    /// Gets or sets the device category
    /// </summary>
    [JsonPropertyName("category")]
    public SystemEventCategory Category { get; set; }

    /// <summary>
    /// Gets or sets the device-specific event code
    /// </summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>
    /// Gets or sets the human-readable description
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets when the event occurred (Unix milliseconds)
    /// </summary>
    [JsonPropertyName("mills")]
    public long Mills { get; set; }

    /// <summary>
    /// Gets or sets the data source identifier
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets additional event details (stored as JSON)
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Gets or sets the original ID from source system for deduplication
    /// </summary>
    [JsonPropertyName("originalId")]
    public string? OriginalId { get; set; }

    /// <summary>
    /// Gets or sets the created at timestamp
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }
}
