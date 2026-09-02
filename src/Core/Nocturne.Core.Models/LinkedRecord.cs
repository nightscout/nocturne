using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a link between a record and its canonical group for deduplication.
/// </summary>
/// <remarks>
/// The <see cref="RecordType"/> property determines which table the <see cref="RecordId"/> references:
/// <see cref="RecordType.Entry"/> maps to <see cref="Entry"/>,
/// <see cref="RecordType.Treatment"/> maps to <see cref="Treatment"/>,
/// <see cref="RecordType.StateSpan"/> maps to <see cref="StateSpan"/>,
/// and other values map to V4 tables.
/// </remarks>
/// <seealso cref="RecordType"/>
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
/// The <c>linked_records.record_type</c> key of every <see cref="RecordType"/>. The per-type
/// constants exist alongside <see cref="Key"/> because EF Core cannot translate a method call
/// inside an expression tree; <c>RecordTypeKeysTests</c> holds the two forms equal.
/// </summary>
public static class RecordTypeKeys
{
    /// <summary><see cref="RecordType.Entry"/></summary>
    public const string Entry = "entry";

    /// <summary><see cref="RecordType.Treatment"/></summary>
    public const string Treatment = "treatment";

    /// <summary><see cref="RecordType.StateSpan"/></summary>
    public const string StateSpan = "statespan";

    /// <summary><see cref="RecordType.SensorGlucose"/></summary>
    public const string SensorGlucose = "sensorglucose";

    /// <summary><see cref="RecordType.Bolus"/></summary>
    public const string Bolus = "bolus";

    /// <summary><see cref="RecordType.CarbIntake"/></summary>
    public const string CarbIntake = "carbintake";

    /// <summary><see cref="RecordType.BGCheck"/></summary>
    public const string BGCheck = "bgcheck";

    /// <summary><see cref="RecordType.DeviceEvent"/></summary>
    public const string DeviceEvent = "deviceevent";

    /// <summary><see cref="RecordType.Note"/></summary>
    public const string Note = "note";

    /// <summary><see cref="RecordType.BolusCalculation"/></summary>
    public const string BolusCalculation = "boluscalculation";

    /// <summary><see cref="RecordType.TempBasal"/></summary>
    public const string TempBasal = "tempbasal";

    /// <summary>
    /// The stored key for <paramref name="recordType"/>.
    /// </summary>
    public static string Key(RecordType recordType) => recordType.ToString().ToLowerInvariant();
}
