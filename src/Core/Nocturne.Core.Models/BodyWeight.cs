using System.Text.Json.Serialization;
using Nocturne.Core.Models.Attributes;

namespace Nocturne.Core.Models;

/// <seealso cref="ProcessableDocumentBase"/>
public class BodyWeight : ProcessableDocumentBase
{
    /// <summary>MongoDB ObjectId.</summary>
    [JsonPropertyName("_id")]
    public override string? Id { get; set; }

    /// <summary>ISO 8601 formatted creation timestamp.</summary>
    [JsonPropertyName("created_at")]
    public override string? CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>Timestamp in milliseconds since Unix epoch.</summary>
    [JsonPropertyName("mills")]
    public override long Mills { get; set; }

    /// <summary>UTC offset in minutes.</summary>
    [JsonPropertyName("utcOffset")]
    public override int? UtcOffset { get; set; }

    /// <summary>Body weight in kilograms.</summary>
    [JsonPropertyName("weightKg")]
    public decimal WeightKg { get; set; }

    /// <summary>Body fat percentage (0-100).</summary>
    [JsonPropertyName("bodyFatPercent")]
    public decimal? BodyFatPercent { get; set; }

    /// <summary>Lean body mass in kilograms.</summary>
    [JsonPropertyName("leanMassKg")]
    public decimal? LeanMassKg { get; set; }

    /// <summary>Device identifier that recorded this measurement.</summary>
    [JsonPropertyName("device")]
    [Sanitizable]
    public string? Device { get; set; }

    [JsonPropertyName("enteredBy")]
    [Sanitizable]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Data source identifier indicating where this measurement originated from.
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
