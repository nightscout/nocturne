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
    ///     The typed payload. Deserialized as a generic element; use helper methods
    ///     or check <see cref="Type"/> to interpret the contents.
    /// </summary>
    [JsonPropertyName("item")]
    public GlookoV3HistoryMeal? Item { get; set; }
}

// ── Meals ────────────────────────────────────────────────────────────

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

// ── Medications ──────────────────────────────────────────────────────

/// <summary>
///     A medication entry (insulin or oral medication).
/// </summary>
public class GlookoV3HistoryMedication
{
    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }

    [JsonPropertyName("medicationName")] public string? MedicationName { get; set; }

    [JsonPropertyName("medicationType")] public string? MedicationType { get; set; }

    [JsonPropertyName("units")] public double? Units { get; set; }

    [JsonPropertyName("insulinType")] public string? InsulinType { get; set; }

    [JsonPropertyName("softDeleted")] public bool? SoftDeleted { get; set; }
}

// ── Exercises ────────────────────────────────────────────────────────

/// <summary>
///     An exercise/activity entry.
/// </summary>
public class GlookoV3HistoryExercise
{
    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }

    [JsonPropertyName("exerciseName")] public string? ExerciseName { get; set; }

    [JsonPropertyName("duration")] public int? Duration { get; set; }

    [JsonPropertyName("intensity")] public string? Intensity { get; set; }

    [JsonPropertyName("softDeleted")] public bool? SoftDeleted { get; set; }
}

// ── Weights ──────────────────────────────────────────────────────────

/// <summary>
///     A weight measurement entry.
/// </summary>
public class GlookoV3HistoryWeight
{
    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }

    [JsonPropertyName("value")] public double? Value { get; set; }

    [JsonPropertyName("unit")] public string? Unit { get; set; }

    [JsonPropertyName("softDeleted")] public bool? SoftDeleted { get; set; }
}

// ── Readings ─────────────────────────────────────────────────────────

/// <summary>
///     A BG reading entry from the histories endpoint.
/// </summary>
public class GlookoV3HistoryReading
{
    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }

    [JsonPropertyName("value")] public double? Value { get; set; }

    [JsonPropertyName("mealTag")] public string? MealTag { get; set; }

    [JsonPropertyName("softDeleted")] public bool? SoftDeleted { get; set; }
}

// ── Notes ────────────────────────────────────────────────────────────

/// <summary>
///     A user note entry.
/// </summary>
public class GlookoV3HistoryNote
{
    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }

    [JsonPropertyName("body")] public string? Body { get; set; }

    [JsonPropertyName("softDeleted")] public bool? SoftDeleted { get; set; }
}

// ── Pump Alerts ──────────────────────────────────────────────────────

/// <summary>
///     A pump alert entry.
/// </summary>
public class GlookoV3HistoryPumpAlert
{
    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }

    [JsonPropertyName("alertType")] public string? AlertType { get; set; }

    [JsonPropertyName("message")] public string? Message { get; set; }

    [JsonPropertyName("softDeleted")] public bool? SoftDeleted { get; set; }
}
