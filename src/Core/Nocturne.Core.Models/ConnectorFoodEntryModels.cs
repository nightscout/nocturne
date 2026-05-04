using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Status for connector food entries awaiting matching or resolution.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectorFoodEntryStatus
{
    /// <summary>Entry has been imported but not yet matched to a Nocturne treatment</summary>
    Pending,

    /// <summary>Entry has been matched to an existing <see cref="Treatment"/></summary>
    Matched,

    /// <summary>Entry was processed and no matching treatment was found; kept as a standalone record</summary>
    Standalone,

    /// <summary>Entry has been deleted</summary>
    Deleted,
}

/// <summary>
/// Represents a connector-imported food entry for matching and attribution.
/// </summary>
/// <seealso cref="ConnectorFoodEntryStatus"/>
/// <seealso cref="Food"/>
public class ConnectorFoodEntry
{
    /// <summary>Unique identifier (UUID v7)</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>Identifier of the connector that imported this entry (e.g., "MyFitnessPal", "Glooko")</summary>
    [JsonPropertyName("connectorSource")]
    public string ConnectorSource { get; set; } = string.Empty;

    /// <summary>ID of the food log entry in the external system</summary>
    [JsonPropertyName("externalEntryId")]
    public string ExternalEntryId { get; set; } = string.Empty;

    /// <summary>ID of the specific food item in the external system's food database</summary>
    [JsonPropertyName("externalFoodId")]
    public string ExternalFoodId { get; set; } = string.Empty;

    /// <summary>
    /// ID of the matched Nocturne <see cref="Food"/> record, populated after deduplication resolves the food item.
    /// </summary>
    [JsonPropertyName("foodId")]
    public Guid? FoodId { get; set; }

    /// <summary>Resolved Nocturne <see cref="Food"/> record (populated in API responses, null in storage)</summary>
    [JsonPropertyName("food")]
    public Food? Food { get; set; }

    /// <summary>When the food was consumed (as reported by the external system)</summary>
    [JsonPropertyName("consumedAt")]
    public DateTimeOffset ConsumedAt { get; set; }

    /// <summary>When the entry was logged in the external app (may differ from <see cref="ConsumedAt"/>)</summary>
    [JsonPropertyName("loggedAt")]
    public DateTimeOffset? LoggedAt { get; set; }

    /// <summary>Meal name or context as provided by the connector (e.g., "Breakfast", "Lunch")</summary>
    [JsonPropertyName("mealName")]
    public string MealName { get; set; } = string.Empty;

    /// <summary>Carbohydrate amount in grams for the consumed serving</summary>
    [JsonPropertyName("carbs")]
    public decimal Carbs { get; set; }

    /// <summary>Protein amount in grams for the consumed serving</summary>
    [JsonPropertyName("protein")]
    public decimal Protein { get; set; }

    /// <summary>Fat amount in grams for the consumed serving</summary>
    [JsonPropertyName("fat")]
    public decimal Fat { get; set; }

    /// <summary>Energy (calories or kilojoules) for the consumed serving</summary>
    [JsonPropertyName("energy")]
    public decimal Energy { get; set; }

    /// <summary>Number of servings consumed</summary>
    [JsonPropertyName("servings")]
    public decimal Servings { get; set; }

    /// <summary>Human-readable description of the serving size (e.g., "1 cup", "100g")</summary>
    [JsonPropertyName("servingDescription")]
    public string? ServingDescription { get; set; }

    /// <summary>Current matching/resolution status of this entry</summary>
    [JsonPropertyName("status")]
    public ConnectorFoodEntryStatus Status { get; set; } = ConnectorFoodEntryStatus.Pending;

    /// <summary>When the entry was matched or marked as standalone/deleted</summary>
    [JsonPropertyName("resolvedAt")]
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>
/// Import payload for connector food entries.
/// Used by connectors to push food log data into Nocturne for matching.
/// </summary>
/// <seealso cref="ConnectorFoodEntry"/>
/// <seealso cref="ConnectorFoodImport"/>
public class ConnectorFoodEntryImport
{
    /// <summary>Identifier of the connector submitting this entry (e.g., "MyFitnessPal")</summary>
    [JsonPropertyName("connectorSource")]
    public string ConnectorSource { get; set; } = string.Empty;

    /// <summary>ID of the food log entry in the external system (used for deduplication)</summary>
    [JsonPropertyName("externalEntryId")]
    public string ExternalEntryId { get; set; } = string.Empty;

    /// <summary>ID of the food item in the external system's food database</summary>
    [JsonPropertyName("externalFoodId")]
    public string ExternalFoodId { get; set; } = string.Empty;

    /// <summary>When the food was consumed</summary>
    [JsonPropertyName("consumedAt")]
    public DateTimeOffset ConsumedAt { get; set; }

    /// <summary>When the entry was logged in the external app</summary>
    [JsonPropertyName("loggedAt")]
    public DateTimeOffset? LoggedAt { get; set; }

    /// <summary>Meal name or context (e.g., "Breakfast")</summary>
    [JsonPropertyName("mealName")]
    public string MealName { get; set; } = string.Empty;

    /// <summary>Carbohydrate amount in grams for the consumed serving</summary>
    [JsonPropertyName("carbs")]
    public decimal Carbs { get; set; }

    /// <summary>Protein amount in grams for the consumed serving</summary>
    [JsonPropertyName("protein")]
    public decimal Protein { get; set; }

    /// <summary>Fat amount in grams for the consumed serving</summary>
    [JsonPropertyName("fat")]
    public decimal Fat { get; set; }

    /// <summary>Energy (calories or kilojoules) for the consumed serving</summary>
    [JsonPropertyName("energy")]
    public decimal Energy { get; set; }

    /// <summary>Number of servings consumed</summary>
    [JsonPropertyName("servings")]
    public decimal Servings { get; set; }

    /// <summary>Human-readable description of the serving size (e.g., "1 cup")</summary>
    [JsonPropertyName("servingDescription")]
    public string? ServingDescription { get; set; }

    /// <summary>
    /// Optional food details for upsert/deduplication of the food item in the Nocturne food database.
    /// When provided, Nocturne will create or update a <see cref="Food"/> record using the external food data.
    /// </summary>
    [JsonPropertyName("food")]
    public ConnectorFoodImport? Food { get; set; }
}

/// <summary>
/// Food details used for connector food deduplication.
/// Provides enough data to create or match a <see cref="Food"/> record in the Nocturne food database.
/// </summary>
/// <seealso cref="ConnectorFoodEntryImport"/>
/// <seealso cref="Food"/>
public class ConnectorFoodImport
{
    /// <summary>ID of the food item in the external system's database (used for deduplication)</summary>
    [JsonPropertyName("externalId")]
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Human-readable name of the food item</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Brand name of the food item (if applicable)</summary>
    [JsonPropertyName("brandName")]
    public string? BrandName { get; set; }

    /// <summary>Carbohydrate amount in grams per standard portion</summary>
    [JsonPropertyName("carbs")]
    public decimal Carbs { get; set; }

    /// <summary>Protein amount in grams per standard portion</summary>
    [JsonPropertyName("protein")]
    public decimal Protein { get; set; }

    /// <summary>Fat amount in grams per standard portion</summary>
    [JsonPropertyName("fat")]
    public decimal Fat { get; set; }

    /// <summary>Energy (calories or kilojoules) per standard portion</summary>
    [JsonPropertyName("energy")]
    public decimal Energy { get; set; }

    /// <summary>Standard portion size (numeric quantity)</summary>
    [JsonPropertyName("portion")]
    public decimal Portion { get; set; }

    /// <summary>Unit of measurement for the portion (e.g., "g", "ml", "oz")</summary>
    [JsonPropertyName("unit")]
    public string? Unit { get; set; }
}
