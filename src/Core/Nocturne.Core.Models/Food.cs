using System.Text.Json.Serialization;
using Nocturne.Core.Models.Serializers;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a Nightscout food record for the API
/// Compatible with the legacy Nightscout food collection
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public class Food
{
    /// <summary>
    /// Gets or sets the MongoDB ObjectId
    /// </summary>
    [JsonPropertyName("_id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the type of record ("food" or "quickpick")
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "food";

    /// <summary>
    /// Gets or sets the food category
    /// </summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the food subcategory
    /// </summary>
    [JsonPropertyName("subcategory")]
    public string Subcategory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the food name
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the portion size
    /// </summary>
    [JsonPropertyName("portion")]
    public double Portion { get; set; }

    /// <summary>
    /// Gets or sets the carbohydrates in grams per portion
    /// </summary>
    [JsonPropertyName("carbs")]
    public double Carbs { get; set; }

    /// <summary>
    /// Gets or sets the fat content in grams per portion
    /// </summary>
    [JsonPropertyName("fat")]
    public double Fat { get; set; }

    /// <summary>
    /// Gets or sets the protein content in grams per portion
    /// </summary>
    [JsonPropertyName("protein")]
    public double Protein { get; set; }

    /// <summary>
    /// Gets or sets the energy content in kilojoules per portion
    /// </summary>
    [JsonPropertyName("energy")]
    public double Energy { get; set; }

    /// <summary>
    /// Gets or sets the glycemic index (1-3: 1=low, 2=medium, 3=high)
    /// </summary>
    [JsonPropertyName("gi")]
    public int Gi { get; set; } = 2;

    /// <summary>
    /// Gets or sets the unit of measurement (g, ml, pcs, oz)
    /// </summary>
    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "g";

    // QuickPick specific properties

    /// <summary>
    /// Gets or sets the foods included in a quickpick (only for type="quickpick")
    /// </summary>
    [JsonPropertyName("foods")]
    public List<QuickPickFood>? Foods { get; set; }

    /// <summary>
    /// Gets or sets whether to hide after use (only for type="quickpick")
    /// </summary>
    [JsonPropertyName("hideafteruse")]
    [JsonConverter(typeof(FlexibleNonNullableBooleanJsonConverter))]
    public bool HideAfterUse { get; set; }

    /// <summary>
    /// Gets or sets whether the quickpick is hidden (only for type="quickpick")
    /// </summary>
    [JsonPropertyName("hidden")]
    [JsonConverter(typeof(FlexibleNonNullableBooleanJsonConverter))]
    public bool Hidden { get; set; }

    /// <summary>
    /// Gets or sets the display position for quickpicks (only for type="quickpick")
    /// </summary>
    [JsonPropertyName("position")]
    public int Position { get; set; } = 99999;

    /// <summary>
    /// Gets or sets the creation timestamp (ISO 8601 format)
    /// This is set by the server when a record is created
    /// </summary>
    [JsonPropertyName("created_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedAt { get; set; }
}

/// <summary>
/// Represents a food item within a quickpick
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public class QuickPickFood
{
    /// <summary>
    /// Gets or sets the food name
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the portion size
    /// </summary>
    [JsonPropertyName("portion")]
    public double Portion { get; set; }

    /// <summary>
    /// Gets or sets the carbohydrates in grams per portion
    /// </summary>
    [JsonPropertyName("carbs")]
    public double Carbs { get; set; }

    /// <summary>
    /// Gets or sets the unit of measurement
    /// </summary>
    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "g";

    /// <summary>
    /// Gets or sets the number of portions for this food in the quickpick
    /// </summary>
    [JsonPropertyName("portions")]
    public double Portions { get; set; } = 1.0;
}
