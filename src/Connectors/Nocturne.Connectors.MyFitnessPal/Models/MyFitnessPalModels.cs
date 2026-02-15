using System.Text.Json.Serialization;

namespace Nocturne.Connectors.MyFitnessPal.Models;

/// <summary>
/// Represents a single day's food diary from MyFitnessPal.
/// </summary>
public class MfpDiaryDay
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("food_entries")]
    public List<MfpFoodEntry> FoodEntries { get; set; } = [];
}

/// <summary>
/// Represents a single food entry from MyFitnessPal.
/// </summary>
public class MfpFoodEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("meal_name")]
    public string MealName { get; set; } = string.Empty;

    [JsonPropertyName("meal_position")]
    public int MealPosition { get; set; }

    [JsonPropertyName("food")]
    public MfpFood? Food { get; set; }

    [JsonPropertyName("serving_size")]
    public MfpServingSize? ServingSize { get; set; }

    [JsonPropertyName("servings")]
    public decimal Servings { get; set; }

    [JsonPropertyName("nutritional_contents")]
    public MfpNutritionalContents? NutritionalContents { get; set; }

    [JsonPropertyName("consumed_at")]
    public string? ConsumedAt { get; set; }

    [JsonPropertyName("logged_at")]
    public string? LoggedAt { get; set; }
}

/// <summary>
/// Represents a food item from MyFitnessPal.
/// </summary>
public class MfpFood
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("brand_name")]
    public string? BrandName { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("serving_sizes")]
    public List<MfpServingSize>? ServingSizes { get; set; }

    [JsonPropertyName("nutritional_contents")]
    public MfpNutritionalContents? NutritionalContents { get; set; }
}

/// <summary>
/// Represents a serving size from MyFitnessPal.
/// </summary>
public class MfpServingSize
{
    [JsonPropertyName("value")]
    public decimal Value { get; set; }

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    [JsonPropertyName("nutrition_multiplier")]
    public decimal NutritionMultiplier { get; set; }
}

/// <summary>
/// Represents nutritional contents from MyFitnessPal.
/// </summary>
public class MfpNutritionalContents
{
    [JsonPropertyName("energy")]
    public MfpEnergy? Energy { get; set; }

    [JsonPropertyName("fat")]
    public decimal? Fat { get; set; }

    [JsonPropertyName("carbohydrates")]
    public decimal? Carbohydrates { get; set; }

    [JsonPropertyName("fiber")]
    public decimal? Fiber { get; set; }

    [JsonPropertyName("sugar")]
    public decimal? Sugar { get; set; }

    [JsonPropertyName("protein")]
    public decimal? Protein { get; set; }

    [JsonPropertyName("sodium")]
    public decimal? Sodium { get; set; }

    [JsonPropertyName("potassium")]
    public decimal? Potassium { get; set; }

    [JsonPropertyName("cholesterol")]
    public decimal? Cholesterol { get; set; }

    [JsonPropertyName("saturated_fat")]
    public decimal? SaturatedFat { get; set; }
}

/// <summary>
/// Represents energy (calories) from MyFitnessPal.
/// </summary>
public class MfpEnergy
{
    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "calories";

    [JsonPropertyName("value")]
    public decimal Value { get; set; }
}
