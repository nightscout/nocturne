using System.Text.Json.Serialization;
using Nocturne.Core.Models.Attributes;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a Nightscout activity record for the API.
/// Compatible with the legacy Nightscout activity collection.
/// </summary>
/// <seealso cref="ProcessableDocumentBase"/>
/// <seealso cref="Entry"/>
/// <seealso cref="Treatment"/>
public class Activity : ProcessableDocumentBase
{
    /// <summary>
    /// Gets or sets the MongoDB ObjectId
    /// </summary>
    [JsonPropertyName("_id")]
    public override string? Id { get; set; }

    /// <summary>
    /// Gets or sets the ISO 8601 formatted creation timestamp
    /// </summary>
    [JsonPropertyName("created_at")]
    public override string? CreatedAt { get; set; } =
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>
    /// Gets or sets the optional timestamp in milliseconds since Unix epoch
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the timestamp in milliseconds since Unix epoch
    /// </summary>
    [JsonPropertyName("mills")]
    public override long Mills { get; set; }

    /// <summary>
    /// Gets or sets the UTC offset in minutes
    /// </summary>
    [JsonPropertyName("utcOffset")]
    public override int? UtcOffset { get; set; }

    /// <summary>
    /// Gets or sets the activity type or category
    /// </summary>
    [JsonPropertyName("type")]
    [Sanitizable]
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets the activity description or notes
    /// </summary>
    [JsonPropertyName("description")]
    [Sanitizable]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the duration of the activity in minutes
    /// </summary>
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    /// <summary>
    /// Gets or sets the intensity level of the activity
    /// </summary>
    [JsonPropertyName("intensity")]
    public string? Intensity { get; set; }

    /// <summary>
    /// Gets or sets additional notes about the activity
    /// </summary>
    [JsonPropertyName("notes")]
    [Sanitizable]
    public string? Notes { get; set; }

    /// <summary>
    /// Gets or sets who entered this activity record
    /// </summary>
    [JsonPropertyName("enteredBy")]
    [Sanitizable]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Gets or sets the user-friendly date string representation
    /// </summary>
    [JsonPropertyName("dateString")]
    public string? DateString { get; set; }

    /// <summary>
    /// Gets or sets the distance covered during the activity
    /// </summary>
    [JsonPropertyName("distance")]
    public double? Distance { get; set; }

    /// <summary>
    /// Gets or sets the units for distance (e.g., "meters", "kilometers", "miles")
    /// </summary>
    [JsonPropertyName("distanceUnits")]
    public string? DistanceUnits { get; set; }

    /// <summary>
    /// Gets or sets the energy expended during the activity (calories)
    /// </summary>
    [JsonPropertyName("energy")]
    public double? Energy { get; set; }

    /// <summary>
    /// Gets or sets the units for energy (e.g., "calories", "kilocalories", "joules")
    /// </summary>
    [JsonPropertyName("energyUnits")]
    public string? EnergyUnits { get; set; }

    /// <summary>
    /// Gets or sets the name/title of the activity
    /// </summary>
    [JsonPropertyName("name")]
    [Sanitizable]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the data source identifier indicating where this activity originated from.
    /// Use constants from <see cref="Core.Constants.DataSources"/> for consistent values.
    /// </summary>
    /// <remarks>
    /// Carried through decomposition onto <see cref="StateSpan.Source"/>,
    /// <see cref="HeartRate.DataSource"/> and <see cref="StepCount.DataSource"/> so a connector's
    /// resume watermark can be scoped to its own records.
    /// </remarks>
    /// <example>
    /// Common values: "nightscout-connector", "glooko-connector", "manual"
    /// </example>
    [JsonPropertyName("data_source")]
    [NocturneOnly]
    public string? DataSource { get; set; }

    /// <summary>
    /// Gets or sets additional properties as a dynamic object
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object>? AdditionalProperties { get; set; }
}
