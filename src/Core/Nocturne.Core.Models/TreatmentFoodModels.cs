using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Food attribution entry linked to a carb intake record.
/// </summary>
/// <seealso cref="Food"/>
/// <seealso cref="TreatmentFoodBreakdown"/>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public class TreatmentFood
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>The V4 carb intake record this food is attributed to.</summary>
    [JsonPropertyName("carbIntakeId")]
    public Guid CarbIntakeId { get; set; }

    /// <summary>The <see cref="Food"/> record this attribution references, or null for unspecified carbs.</summary>
    [JsonPropertyName("foodId")]
    public Guid? FoodId { get; set; }

    [JsonPropertyName("portions")]
    public decimal Portions { get; set; }

    /// <summary>Total carbohydrates from this food attribution (grams).</summary>
    [JsonPropertyName("carbs")]
    public decimal Carbs { get; set; }

    /// <summary>Time offset in minutes from the carb intake timestamp (for staggered eating).</summary>
    [JsonPropertyName("timeOffsetMinutes")]
    public int TimeOffsetMinutes { get; set; }

    /// <summary>Optional note about this food attribution.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>Denormalized food name from the <see cref="Food"/> record.</summary>
    [JsonPropertyName("foodName")]
    public string? FoodName { get; set; }

    /// <summary>Denormalized carbohydrates per portion from the <see cref="Food"/> record.</summary>
    [JsonPropertyName("carbsPerPortion")]
    public decimal? CarbsPerPortion { get; set; }

    /// <summary>Denormalized fat per portion from the <see cref="Food"/> record.</summary>
    [JsonPropertyName("fatPerPortion")]
    public decimal? FatPerPortion { get; set; }

    /// <summary>Denormalized protein per portion from the <see cref="Food"/> record.</summary>
    [JsonPropertyName("proteinPerPortion")]
    public decimal? ProteinPerPortion { get; set; }
}

/// <summary>
/// User-specific favorite <see cref="Food"/> entry for quick selection.
/// </summary>
/// <seealso cref="Food"/>
public class UserFoodFavorite
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>User who favorited this food.</summary>
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>The favorited <see cref="Food"/> record ID.</summary>
    [JsonPropertyName("foodId")]
    public Guid FoodId { get; set; }
}

/// <summary>
/// Response model for how many meal attributions reference a specific <see cref="Food"/>.
/// </summary>
public class FoodAttributionCount
{
    /// <summary>The <see cref="Food"/> record ID.</summary>
    [JsonPropertyName("foodId")]
    public string FoodId { get; set; } = string.Empty;

    /// <summary>Number of <see cref="TreatmentFood"/> attributions referencing this food.</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>
/// Aggregated food breakdown for a carb intake record, showing attributed vs unspecified carbs.
/// </summary>
/// <seealso cref="TreatmentFood"/>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public class TreatmentFoodBreakdown
{
    [JsonPropertyName("carbIntakeId")]
    public Guid CarbIntakeId { get; set; }

    /// <summary>Individual <see cref="TreatmentFood"/> attributions for this carb intake.</summary>
    [JsonPropertyName("foods")]
    public List<TreatmentFood> Foods { get; set; } = [];

    /// <summary>Whether any foods have been attributed to this carb intake.</summary>
    [JsonPropertyName("isAttributed")]
    public bool IsAttributed { get; set; }

    /// <summary>Total carbs accounted for by food attributions (grams).</summary>
    [JsonPropertyName("attributedCarbs")]
    public decimal AttributedCarbs { get; set; }

    /// <summary>Carbs not yet attributed to specific foods (grams).</summary>
    [JsonPropertyName("unspecifiedCarbs")]
    public decimal UnspecifiedCarbs { get; set; }
}

