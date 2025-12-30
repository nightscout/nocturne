using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a food attribution entry linked to a treatment.
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public class TreatmentFood
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("treatmentId")]
    public Guid TreatmentId { get; set; }

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
/// Aggregated food breakdown for a treatment.
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public class TreatmentFoodBreakdown
{
    [JsonPropertyName("treatmentId")]
    public Guid TreatmentId { get; set; }

    [JsonPropertyName("foods")]
    public List<TreatmentFood> Foods { get; set; } = [];

    [JsonPropertyName("isAttributed")]
    public bool IsAttributed { get; set; }

    [JsonPropertyName("attributedCarbs")]
    public decimal AttributedCarbs { get; set; }

    [JsonPropertyName("unspecifiedCarbs")]
    public decimal UnspecifiedCarbs { get; set; }
}

/// <summary>
/// Meal treatment response for attribution workflows.
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public class MealTreatment
{
    [JsonPropertyName("treatment")]
    public Treatment Treatment { get; set; } = new();

    [JsonPropertyName("foods")]
    public List<TreatmentFood> Foods { get; set; } = [];

    [JsonPropertyName("isAttributed")]
    public bool IsAttributed { get; set; }

    [JsonPropertyName("attributedCarbs")]
    public decimal AttributedCarbs { get; set; }

    [JsonPropertyName("unspecifiedCarbs")]
    public decimal UnspecifiedCarbs { get; set; }
}
