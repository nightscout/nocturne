using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Status for connector food entries awaiting matching or resolution.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectorFoodEntryStatus
{
    Pending,
    Matched,
    Standalone,
    Deleted,
}

/// <summary>
/// Represents a connector-imported food entry for matching and attribution.
/// </summary>
public class ConnectorFoodEntry
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("connectorSource")]
    public string ConnectorSource { get; set; } = string.Empty;

    [JsonPropertyName("externalEntryId")]
    public string ExternalEntryId { get; set; } = string.Empty;

    [JsonPropertyName("externalFoodId")]
    public string ExternalFoodId { get; set; } = string.Empty;

    [JsonPropertyName("foodId")]
    public Guid? FoodId { get; set; }

    [JsonPropertyName("food")]
    public Food? Food { get; set; }

    [JsonPropertyName("consumedAt")]
    public DateTimeOffset ConsumedAt { get; set; }

    [JsonPropertyName("loggedAt")]
    public DateTimeOffset? LoggedAt { get; set; }

    [JsonPropertyName("mealName")]
    public string MealName { get; set; } = string.Empty;

    [JsonPropertyName("carbs")]
    public decimal Carbs { get; set; }

    [JsonPropertyName("protein")]
    public decimal Protein { get; set; }

    [JsonPropertyName("fat")]
    public decimal Fat { get; set; }

    [JsonPropertyName("energy")]
    public decimal Energy { get; set; }

    [JsonPropertyName("servings")]
    public decimal Servings { get; set; }

    [JsonPropertyName("servingDescription")]
    public string? ServingDescription { get; set; }

    [JsonPropertyName("status")]
    public ConnectorFoodEntryStatus Status { get; set; } = ConnectorFoodEntryStatus.Pending;

    [JsonPropertyName("matchedTreatmentId")]
    public Guid? MatchedTreatmentId { get; set; }

    [JsonPropertyName("resolvedAt")]
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>
/// Import payload for connector food entries.
/// </summary>
public class ConnectorFoodEntryImport
{
    [JsonPropertyName("connectorSource")]
    public string ConnectorSource { get; set; } = string.Empty;

    [JsonPropertyName("externalEntryId")]
    public string ExternalEntryId { get; set; } = string.Empty;

    [JsonPropertyName("externalFoodId")]
    public string ExternalFoodId { get; set; } = string.Empty;

    [JsonPropertyName("consumedAt")]
    public DateTimeOffset ConsumedAt { get; set; }

    [JsonPropertyName("loggedAt")]
    public DateTimeOffset? LoggedAt { get; set; }

    [JsonPropertyName("mealName")]
    public string MealName { get; set; } = string.Empty;

    [JsonPropertyName("carbs")]
    public decimal Carbs { get; set; }

    [JsonPropertyName("protein")]
    public decimal Protein { get; set; }

    [JsonPropertyName("fat")]
    public decimal Fat { get; set; }

    [JsonPropertyName("energy")]
    public decimal Energy { get; set; }

    [JsonPropertyName("servings")]
    public decimal Servings { get; set; }

    [JsonPropertyName("servingDescription")]
    public string? ServingDescription { get; set; }

    [JsonPropertyName("food")]
    public ConnectorFoodImport? Food { get; set; }
}

/// <summary>
/// Food details used for connector food deduplication.
/// </summary>
public class ConnectorFoodImport
{
    [JsonPropertyName("externalId")]
    public string ExternalId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("brandName")]
    public string? BrandName { get; set; }

    [JsonPropertyName("carbs")]
    public decimal Carbs { get; set; }

    [JsonPropertyName("protein")]
    public decimal Protein { get; set; }

    [JsonPropertyName("fat")]
    public decimal Fat { get; set; }

    [JsonPropertyName("energy")]
    public decimal Energy { get; set; }

    [JsonPropertyName("portion")]
    public decimal Portion { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }
}
