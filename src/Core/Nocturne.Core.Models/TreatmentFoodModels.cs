using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a food attribution entry linked to a carb intake record.
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public class TreatmentFood
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("carbIntakeId")]
    public Guid CarbIntakeId { get; set; }

    [JsonPropertyName("foodId")]
    public Guid? FoodId { get; set; }

    [JsonPropertyName("portions")]
    public decimal Portions { get; set; }

    [JsonPropertyName("carbs")]
    public decimal Carbs { get; set; }

    [JsonPropertyName("timeOffsetMinutes")]
    public int TimeOffsetMinutes { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("foodName")]
    public string? FoodName { get; set; }

    [JsonPropertyName("carbsPerPortion")]
    public decimal? CarbsPerPortion { get; set; }
}

/// <summary>
/// User-specific favorite food entry.
/// </summary>
public class UserFoodFavorite
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("foodId")]
    public Guid FoodId { get; set; }
}

/// <summary>
/// Response model for how many meal attributions reference a specific food.
/// </summary>
public class FoodAttributionCount
{
    [JsonPropertyName("foodId")]
    public string FoodId { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>
/// Aggregated food breakdown for a carb intake record.
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public class TreatmentFoodBreakdown
{
    [JsonPropertyName("carbIntakeId")]
    public Guid CarbIntakeId { get; set; }

    [JsonPropertyName("foods")]
    public List<TreatmentFood> Foods { get; set; } = [];

    [JsonPropertyName("isAttributed")]
    public bool IsAttributed { get; set; }

    [JsonPropertyName("attributedCarbs")]
    public decimal AttributedCarbs { get; set; }

    [JsonPropertyName("unspecifiedCarbs")]
    public decimal UnspecifiedCarbs { get; set; }
}

