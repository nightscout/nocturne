using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Glooko.Models;

/// <summary>
///     Response from /api/v3/users/summary/histories endpoint.
///     Contains a flat list of typed history entries (meals, medications, exercises, etc.).
/// </summary>
public class GlookoV3HistoriesResponse
{
    [JsonPropertyName("histories")] public GlookoV3HistoryEntry[]? Histories { get; set; }
}

/// <summary>
///     A single typed history entry. The <see cref="Type"/> field indicates the data type
///     (e.g., "meals", "medications") and the corresponding item property contains the data.
/// </summary>
public class GlookoV3HistoryEntry
{
    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("softDeleted")] public bool? SoftDeleted { get; set; }

    [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; set; }

    [JsonPropertyName("updatedBy")] public string? UpdatedBy { get; set; }

    /// <summary>
    ///     Read as a meal whatever <see cref="Type"/> says; only entries whose type is <c>meals</c>
    ///     are used (see <c>GlookoV4TreatmentMapper.ExtractMeals</c>).
    /// </summary>
    [JsonPropertyName("item")]
    public GlookoV3HistoryMeal? Item { get; set; }
}

/// <summary>
///     A meal entry containing one or more food items with per-food nutritional data.
///     Used as the <see cref="GlookoV3HistoryEntry.Item"/> when type == "meals".
/// </summary>
public class GlookoV3HistoryMeal
{
    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    /// <summary>Meal type: "breakfast", "lunch", "dinner", "snack"</summary>
    [JsonPropertyName("type")] public string? MealType { get; set; }

    [JsonPropertyName("carbs")] public double? Carbs { get; set; }

    [JsonPropertyName("fat")] public double? Fat { get; set; }

    [JsonPropertyName("protein")] public double? Protein { get; set; }

    [JsonPropertyName("calories")] public double? Calories { get; set; }

    [JsonPropertyName("foods")] public GlookoV3HistoryFood[]? Foods { get; set; }

    [JsonPropertyName("softDeleted")] public bool? SoftDeleted { get; set; }

    [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; set; }

    [JsonPropertyName("updatedBy")] public string? UpdatedBy { get; set; }
}

/// <summary>
///     An individual food item within a V3 history meal.
/// </summary>
public class GlookoV3HistoryFood
{
    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("brand")] public string? Brand { get; set; }

    [JsonPropertyName("carbs")] public double? Carbs { get; set; }

    [JsonPropertyName("fat")] public double? Fat { get; set; }

    [JsonPropertyName("protein")] public double? Protein { get; set; }

    [JsonPropertyName("calories")] public double? Calories { get; set; }

    [JsonPropertyName("servingQuantity")] public double? ServingQuantity { get; set; }

    [JsonPropertyName("servingUnit")] public string? ServingUnit { get; set; }

    [JsonPropertyName("numberOfServings")] public double? NumberOfServings { get; set; }

    [JsonPropertyName("externalId")] public string? ExternalId { get; set; }

    [JsonPropertyName("source")] public string? Source { get; set; }

    [JsonPropertyName("imageUrl")] public string? ImageUrl { get; set; }

    [JsonPropertyName("softDeleted")] public bool? SoftDeleted { get; set; }
}
