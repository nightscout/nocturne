using System.Text.Json.Serialization;
using Nocturne.Core.Models.Attributes;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a body weight measurement record.
/// </summary>
/// <seealso cref="ProcessableDocumentBase"/>
public class BodyWeight : ProcessableDocumentBase
{
    /// <summary>Gets or sets the MongoDB ObjectId.</summary>
    [JsonPropertyName("_id")]
    public override string? Id { get; set; }

    /// <summary>Gets or sets the ISO 8601 formatted creation timestamp.</summary>
    [JsonPropertyName("created_at")]
    public override string? CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>Gets or sets the timestamp in milliseconds since Unix epoch.</summary>
    [JsonPropertyName("mills")]
    public override long Mills { get; set; }

    /// <summary>Gets or sets the UTC offset in minutes.</summary>
    [JsonPropertyName("utcOffset")]
    public override int? UtcOffset { get; set; }

    /// <summary>Gets or sets the body weight in kilograms.</summary>
    [JsonPropertyName("weightKg")]
    public decimal WeightKg { get; set; }

    /// <summary>Gets or sets the body fat percentage (0-100).</summary>
    [JsonPropertyName("bodyFatPercent")]
    public decimal? BodyFatPercent { get; set; }

    /// <summary>Gets or sets the lean body mass in kilograms.</summary>
    [JsonPropertyName("leanMassKg")]
    public decimal? LeanMassKg { get; set; }

    /// <summary>Gets or sets the device identifier that recorded this measurement.</summary>
    [JsonPropertyName("device")]
    [Sanitizable]
    public string? Device { get; set; }

    /// <summary>Gets or sets who entered this record.</summary>
    [JsonPropertyName("enteredBy")]
    [Sanitizable]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Gets or sets the data source identifier indicating where this measurement originated from.
    /// Serialized as "data_source". Nocturne-only field, not present in legacy Nightscout.
    /// </summary>
    [JsonPropertyName("data_source")]
    [NocturneOnly]
    public string? DataSource { get; set; }

    /// <summary>
    /// Stable per-source identifier for synchronization. When paired with <see cref="DataSource"/>,
    /// re-uploading the same measurement updates the existing record in place rather than duplicating it.
    /// </summary>
    [JsonPropertyName("syncIdentifier")]
    [NocturneOnly]
    public string? SyncIdentifier { get; set; }
}
