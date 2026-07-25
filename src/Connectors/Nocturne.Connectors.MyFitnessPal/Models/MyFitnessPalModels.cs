using System.Text.Json.Serialization;

namespace Nocturne.Connectors.MyFitnessPal.Models;

/// <summary>
/// Response from the MyFitnessPal OAuth2 token endpoint.
/// </summary>
public class MfpTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }
}

/// <summary>
/// GraphQL response envelope.
/// </summary>
public class MfpGraphQlResponse<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("errors")]
    public List<MfpGraphQlError>? Errors { get; set; }
}

public class MfpGraphQlError
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Root data of the SyncFoodDiaryEntries operation.
/// </summary>
public class MfpBatchSyncData
{
    [JsonPropertyName("batchSync")]
    public MfpBatchSync? BatchSync { get; set; }
}

public class MfpBatchSync
{
    [JsonPropertyName("foodDiaryEntryConnection")]
    public MfpFoodDiaryEntryConnection? FoodDiaryEntryConnection { get; set; }
}

/// <summary>
/// Relay-style connection of food diary entries. Field names follow the aliases in the
/// operation document.
/// </summary>
public class MfpFoodDiaryEntryConnection
{
    [JsonPropertyName("foodDiaryEntryEdges")]
    public List<MfpFoodDiaryEntryEdge> FoodDiaryEntryEdges { get; set; } = [];

    [JsonPropertyName("foodDiaryEntryPaging")]
    public MfpPageInfo? FoodDiaryEntryPaging { get; set; }

    [JsonPropertyName("foodDiaryEntrySyncInfo")]
    public MfpSyncConnectionInfo? FoodDiaryEntrySyncInfo { get; set; }
}

public class MfpFoodDiaryEntryEdge
{
    [JsonPropertyName("foodDiaryEntryNode")]
    public MfpFoodDiaryEntryNode? FoodDiaryEntryNode { get; set; }

    [JsonPropertyName("foodDiaryEntryEdgeSync")]
    public MfpSyncEdgeInfo? FoodDiaryEntryEdgeSync { get; set; }
}

/// <summary>
/// Per-edge sync metadata. <see cref="Operation"/> is one of CREATE, UPDATE, UPSERT or DELETE.
/// </summary>
public class MfpSyncEdgeInfo
{
    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    [JsonPropertyName("lastModifiedAt")]
    public string? LastModifiedAt { get; set; }
}

public class MfpPageInfo
{
    [JsonPropertyName("hasPreviousPage")]
    public bool HasPreviousPage { get; set; }

    [JsonPropertyName("hasNextPage")]
    public bool HasNextPage { get; set; }

    [JsonPropertyName("startCursor")]
    public string? StartCursor { get; set; }

    [JsonPropertyName("endCursor")]
    public string? EndCursor { get; set; }
}

public class MfpSyncConnectionInfo
{
    [JsonPropertyName("startSyncCursor")]
    public string? StartSyncCursor { get; set; }

    [JsonPropertyName("endSyncCursor")]
    public string? EndSyncCursor { get; set; }

    [JsonPropertyName("totalEdges")]
    public int TotalEdges { get; set; }
}

/// <summary>
/// A food diary entry node. Fields below <see cref="Id"/> come from the
/// <c>... on ActiveFoodDiaryEntry</c> inline fragment and are absent for other entry states.
/// </summary>
public class MfpFoodDiaryEntryNode
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("consumedAt")]
    public string? ConsumedAt { get; set; }

    [JsonPropertyName("eatingOccasion")]
    public string? EatingOccasion { get; set; }

    [JsonPropertyName("eatingOccasionSlot")]
    public int EatingOccasionSlot { get; set; }

    [JsonPropertyName("loggedAt")]
    public string? LoggedAt { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("servingSize")]
    public MfpServingSize? ServingSize { get; set; }

    [JsonPropertyName("food")]
    public MfpFood? Food { get; set; }

    /// <summary>
    /// Nutrients for the logged quantity, already multiplied out. Prefer this over
    /// <see cref="MfpFood.NutrientSet"/>, which is per serving.
    /// </summary>
    [JsonPropertyName("consumedNutrientSet")]
    public MfpNutrientSet? ConsumedNutrientSet { get; set; }
}

public class MfpServingSize
{
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("nutritionMultiplier")]
    public decimal NutritionMultiplier { get; set; }

    [JsonPropertyName("isFraction")]
    public bool IsFraction { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }
}

/// <summary>
/// The logged food. <c>__typename</c> is either IndividualFood or MealIngredient;
/// <see cref="MealFoodId"/> and <see cref="MealIngredientId"/> are only set for the latter.
/// </summary>
public class MfpFood
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("brand")]
    public string? Brand { get; set; }

    [JsonPropertyName("isVerified")]
    public bool IsVerified { get; set; }

    [JsonPropertyName("grams")]
    public decimal? Grams { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("mealFoodId")]
    public string? MealFoodId { get; set; }

    [JsonPropertyName("mealIngredientId")]
    public string? MealIngredientId { get; set; }

    [JsonPropertyName("nutrientSet")]
    public MfpNutrientSet? NutrientSet { get; set; }

    [JsonPropertyName("servingSizes")]
    public List<MfpServingSize>? ServingSizes { get; set; }
}

public class MfpNutrientSet
{
    [JsonPropertyName("calories")]
    public decimal? Calories { get; set; }

    [JsonPropertyName("protein")]
    public decimal? Protein { get; set; }

    [JsonPropertyName("carbs")]
    public decimal? Carbs { get; set; }

    [JsonPropertyName("fat")]
    public decimal? Fat { get; set; }

    [JsonPropertyName("fiber")]
    public decimal? Fiber { get; set; }

    [JsonPropertyName("sugar")]
    public decimal? Sugar { get; set; }

    [JsonPropertyName("sugarAlcohols")]
    public decimal? SugarAlcohols { get; set; }

    [JsonPropertyName("saturatedFat")]
    public decimal? SaturatedFat { get; set; }

    [JsonPropertyName("sodium")]
    public decimal? Sodium { get; set; }
}
