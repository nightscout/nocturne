using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Glooko.Models;

public class GlookoBatchData
{
    [JsonPropertyName("foods")] public GlookoFood[]? Foods { get; set; }

    [JsonPropertyName("scheduledBasals")] public GlookoBasal[]? ScheduledBasals { get; set; }

    [JsonPropertyName("normalBoluses")] public GlookoBolus[]? NormalBoluses { get; set; }

    [JsonPropertyName("readings")] public GlookoCgmReading[]? Readings { get; set; }

    [JsonPropertyName("meterReadings")] public GlookoMeterReading[]? MeterReadings { get; set; }

    [JsonPropertyName("suspendBasals")] public GlookoSuspendBasal[]? SuspendBasals { get; set; }

    [JsonPropertyName("temporaryBasals")] public GlookoTempBasal[]? TempBasals { get; set; }
}

public class GlookoFood
{
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("pumpTimestamp")] public string PumpTimestamp { get; set; } = string.Empty;

    [JsonPropertyName("carbs")] public double Carbs { get; set; }

    [JsonPropertyName("carbohydrateGrams")]
    public double CarbohydrateGrams { get; set; }

    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;

    // -- Rich food data from v2/foods API ---------------------------------

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("fat")] public double? Fat { get; set; }

    [JsonPropertyName("protein")] public double? Protein { get; set; }

    [JsonPropertyName("calories")] public double? Calories { get; set; }

    [JsonPropertyName("servingQuantity")] public double? ServingQuantity { get; set; }

    [JsonPropertyName("servingUnit")] public string? ServingUnit { get; set; }

    [JsonPropertyName("numberOfServings")] public double? NumberOfServings { get; set; }

    [JsonPropertyName("imageUrl")] public string? ImageUrl { get; set; }

    [JsonPropertyName("brand")] public string? Brand { get; set; }

    [JsonPropertyName("source")] public string? Source { get; set; }

    [JsonPropertyName("locale")] public string? Locale { get; set; }

    [JsonPropertyName("mealGuid")] public string? MealGuid { get; set; }

    [JsonPropertyName("externalId")] public string? ExternalId { get; set; }

    [JsonPropertyName("softDeleted")] public bool? SoftDeleted { get; set; }

    [JsonPropertyName("favoriteGuid")] public string? FavoriteGuid { get; set; }

    [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; set; }

    [JsonPropertyName("manuallyEnteredText")]
    public JsonElement? ManuallyEnteredText { get; set; }
}

public class GlookoBasal
{
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("pumpTimestamp")] public string PumpTimestamp { get; set; } = string.Empty;

    [JsonPropertyName("rate")] public double Rate { get; set; }

    [JsonPropertyName("duration")] public int Duration { get; set; }

    [JsonPropertyName("startTime")] public int? StartTime { get; set; }

    [JsonPropertyName("pumpTimestampUtcOffset")]
    public string? PumpTimestampUtcOffset { get; set; }
}

public class GlookoBolus
{
    [JsonPropertyName("pumpTimestamp")] public string PumpTimestamp { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("insulinDelivered")] public double InsulinDelivered { get; set; }

    [JsonPropertyName("carbsInput")] public double CarbsInput { get; set; }

    [JsonPropertyName("deliveredUnits")] public double DeliveredUnits { get; set; }

    [JsonPropertyName("programmedUnits")] public double ProgrammedUnits { get; set; }
}

public class GlookoCgmReading
{
    [JsonPropertyName("display_time")] public string Timestamp { get; set; } = string.Empty;

    /// <summary>
    /// Glucose value in mg/dL × 100 (integer encoding for 2-decimal precision).
    /// Must be divided by 100 to get actual mg/dL.
    /// </summary>
    [JsonPropertyName("bg_value")] public double Value { get; set; }

    [JsonPropertyName("trend")] public string? Trend { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("soft_deleted")] public bool? SoftDeleted { get; set; }
}

public class GlookoSuspendBasal
{
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("duration")] public int Duration { get; set; }

    [JsonPropertyName("suspendReason")] public string? SuspendReason { get; set; }
}

public class GlookoTempBasal
{
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("rate")] public double Rate { get; set; }

    [JsonPropertyName("duration")] public int Duration { get; set; }

    [JsonPropertyName("percent")] public int? Percent { get; set; }

    [JsonPropertyName("tempBasalType")] public string? TempBasalType { get; set; }
}

public class GlookoMeterReading
{
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;

    /// <summary>
    /// Glucose value in mg/dL × 100 (integer encoding for 2-decimal precision).
    /// Must be divided by 100 to get actual mg/dL.
    /// </summary>
    [JsonPropertyName("value")] public double Value { get; set; }

    [JsonPropertyName("meterUnits")] public string? MeterUnits { get; set; }

    [JsonPropertyName("meterMealTag")] public int? MeterMealTag { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("softDeleted")] public bool? SoftDeleted { get; set; }

    [JsonPropertyName("meterGuid")] public string? MeterGuid { get; set; }
}
