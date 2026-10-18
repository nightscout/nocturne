using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Link between a record and its canonical group for deduplication.
/// </summary>
/// <remarks>
/// The <see cref="RecordType"/> property determines which table the <see cref="RecordId"/> references:
/// <see cref="RecordType.StateSpan"/> maps to <see cref="StateSpan"/>,
/// and other values map to V4 tables.
/// </remarks>
/// <seealso cref="RecordType"/>
public class LinkedRecord
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Canonical group identifier shared by all records representing the same event
    /// </summary>
    [JsonPropertyName("canonicalId")]
    public Guid CanonicalId { get; set; }

    [JsonPropertyName("recordType")]
    public RecordType RecordType { get; set; }

    [JsonPropertyName("recordId")]
    public Guid RecordId { get; set; }

    /// <summary>
    /// Timestamp from the source record (Mills)
    /// </summary>
    [JsonPropertyName("sourceTimestamp")]
    public long SourceTimestamp { get; set; }

    /// <summary>
    /// Data source identifier (e.g., "glooko-connector", "mylife-connector")
    /// </summary>
    [JsonPropertyName("dataSource")]
    public string DataSource { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is the primary record in the canonical group (earliest timestamp)
    /// </summary>
    [JsonPropertyName("isPrimary")]
    public bool IsPrimary { get; set; }

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
    /// State span (pump mode, connectivity, override, profile)
    /// </summary>
    StateSpan,

    /// <summary>
    /// V4 sensor glucose reading
    /// </summary>
    SensorGlucose,

    /// <summary>
    /// V4 bolus (insulin delivery)
    /// </summary>
    Bolus,

    /// <summary>
    /// V4 carb intake
    /// </summary>
    CarbIntake,

    /// <summary>
    /// V4 blood glucose check
    /// </summary>
    BGCheck,

    /// <summary>
    /// V4 device event (site change, sensor start, etc.)
    /// </summary>
    DeviceEvent,

    /// <summary>
    /// V4 note
    /// </summary>
    Note,

    /// <summary>
    /// V4 bolus calculation
    /// </summary>
    BolusCalculation,

    /// <summary>
    /// V4 temporary basal rate
    /// </summary>
    TempBasal
}

/// <summary>
/// Owns the <c>linked_records.record_type</c> key of every <see cref="RecordType"/>.
/// </summary>
public static class RecordTypeKeys
{
    /// <summary>
    /// The stored key for <paramref name="recordType"/>.
    /// </summary>
    public static string Key(RecordType recordType) => recordType.ToString().ToLowerInvariant();
}
