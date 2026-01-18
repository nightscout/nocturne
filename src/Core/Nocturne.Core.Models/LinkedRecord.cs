using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a link between a record and its canonical group for deduplication
/// </summary>
public class LinkedRecord
{
    /// <summary>
    /// Gets or sets the unique identifier for this link
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the canonical group identifier shared by all records representing the same event
    /// </summary>
    [JsonPropertyName("canonicalId")]
    public Guid CanonicalId { get; set; }

    /// <summary>
    /// Gets or sets the type of record being linked
    /// </summary>
    [JsonPropertyName("recordType")]
    public RecordType RecordType { get; set; }

    /// <summary>
    /// Gets or sets the ID of the linked record (entry, treatment, or state span)
    /// </summary>
    [JsonPropertyName("recordId")]
    public Guid RecordId { get; set; }

    /// <summary>
    /// Gets or sets the timestamp from the source record (Mills)
    /// </summary>
    [JsonPropertyName("sourceTimestamp")]
    public long SourceTimestamp { get; set; }

    /// <summary>
    /// Gets or sets the data source identifier (e.g., "glooko-connector", "mylife-connector")
    /// </summary>
    [JsonPropertyName("dataSource")]
    public string DataSource { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this is the primary record in the canonical group (earliest timestamp)
    /// </summary>
    [JsonPropertyName("isPrimary")]
    public bool IsPrimary { get; set; }

    /// <summary>
    /// Gets or sets when this link was created
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Types of records that can be deduplicated
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RecordType>))]
public enum RecordType
{
    /// <summary>
    /// Glucose entry (SGV, MBG, calibration)
    /// </summary>
    Entry,

    /// <summary>
    /// Treatment (bolus, carbs, temp basal, etc.)
    /// </summary>
    Treatment,

    /// <summary>
    /// State span (pump mode, connectivity, override, profile)
    /// </summary>
    StateSpan
}
