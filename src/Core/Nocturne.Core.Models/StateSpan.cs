using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a time-ranged system state (pump mode, connectivity, override, profile)
/// </summary>
public class StateSpan
{
    /// <summary>
    /// Gets or sets the unique identifier (UUID or original source ID)
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the state category
    /// </summary>
    [JsonPropertyName("category")]
    public StateSpanCategory Category { get; set; }

    /// <summary>
    /// Gets or sets the state value within the category
    /// </summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>
    /// Gets or sets when this state began (Unix milliseconds)
    /// </summary>
    [JsonPropertyName("startMills")]
    public long StartMills { get; set; }

    /// <summary>
    /// Gets or sets when this state ended (Unix milliseconds, null = active)
    /// </summary>
    [JsonPropertyName("endMills")]
    public long? EndMills { get; set; }

    /// <summary>
    /// Gets or sets the data source identifier
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets category-specific metadata (stored as JSON)
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

    /// <summary>
    /// Gets or sets the updated at timestamp
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Returns true if this state span is currently active (no end time)
    /// </summary>
    [JsonIgnore]
    public bool IsActive => !EndMills.HasValue;

    /// <summary>
    /// Gets or sets the canonical group ID for deduplication.
    /// Records with the same CanonicalId represent the same underlying event from different sources.
    /// </summary>
    [JsonPropertyName("canonicalId")]
    public Guid? CanonicalId { get; set; }

    /// <summary>
    /// Gets or sets the list of data sources that contributed to this unified record.
    /// Only populated when returning merged/unified DTOs.
    /// </summary>
    [JsonPropertyName("sources")]
    public string[]? Sources { get; set; }
}
