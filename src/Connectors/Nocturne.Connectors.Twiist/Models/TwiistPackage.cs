using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Twiist.Models;

/// <summary>
/// Top-level response from the Twiist follower API package endpoint.
/// </summary>
public class TwiistPackage
{
    [JsonPropertyName("pwdId")]
    public string? PwdId { get; set; }

    [JsonPropertyName("pwdNickname")]
    public string? PwdNickname { get; set; }

    [JsonPropertyName("status")]
    public TwiistStatus? Status { get; set; }
}

public class TwiistStatus
{
    [JsonPropertyName("date")]
    public DateTime? Date { get; set; }

    [JsonPropertyName("summary")]
    public TwiistSummary? Summary { get; set; }

    [JsonPropertyName("details")]
    public TwiistDetails? Details { get; set; }

    [JsonPropertyName("loopAlgorithm")]
    public TwiistLoopAlgorithm? LoopAlgorithm { get; set; }

    [JsonPropertyName("events")]
    public List<TwiistEvent>? Events { get; set; }

    [JsonPropertyName("insulinHistory")]
    public List<TwiistInsulinDose>? InsulinHistory { get; set; }

    [JsonPropertyName("mealHistory")]
    public List<TwiistMeal>? MealHistory { get; set; }

    [JsonPropertyName("glucoseHistory")]
    public TwiistRawBlob? GlucoseHistory { get; set; }

    [JsonPropertyName("insulinDelivery")]
    public TwiistRawBlob? InsulinDelivery { get; set; }

    [JsonPropertyName("activeInsulin")]
    public TwiistRawBlob? ActiveInsulin { get; set; }

    [JsonPropertyName("activeCarbohydrates")]
    public TwiistRawBlob? ActiveCarbohydrates { get; set; }

    [JsonPropertyName("correctionRange")]
    public TwiistRawBlob? CorrectionRange { get; set; }

    [JsonPropertyName("glucoseForecast")]
    public TwiistRawBlob? GlucoseForecast { get; set; }
}

public class TwiistRawBlob
{
    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

public class TwiistSummary
{
    [JsonPropertyName("lastGlucoseValue")]
    public decimal? LastGlucoseValue { get; set; }

    [JsonPropertyName("lastGlucoseTrend")]
    public string? LastGlucoseTrend { get; set; }

    [JsonPropertyName("lastGlucoseDate")]
    public DateTime? LastGlucoseDate { get; set; }

    [JsonPropertyName("pumpBattery")]
    public decimal? PumpBattery { get; set; }

    [JsonPropertyName("pumpCassette")]
    public decimal? PumpCassette { get; set; }

    [JsonPropertyName("activeBasalRate")]
    public decimal? ActiveBasalRate { get; set; }

    [JsonPropertyName("iob")]
    public decimal? Iob { get; set; }

    [JsonPropertyName("cob")]
    public decimal? Cob { get; set; }
}

public class TwiistDetails
{
    [JsonPropertyName("activeCarbs")]
    public decimal? ActiveCarbs { get; set; }

    [JsonPropertyName("activeInsulin")]
    public decimal? ActiveInsulin { get; set; }

    [JsonPropertyName("basalRate")]
    public decimal? BasalRate { get; set; }

    [JsonPropertyName("scheduledBasalRate")]
    public decimal? ScheduledBasalRate { get; set; }
}

public class TwiistLoopAlgorithm
{
    [JsonPropertyName("closedLoopSetting")]
    public string? ClosedLoopSetting { get; set; }

    [JsonPropertyName("lastLoopRunDate")]
    public DateTime? LastLoopRunDate { get; set; }

    [JsonPropertyName("lastLoopError")]
    public string? LastLoopError { get; set; }
}

public class TwiistEvent
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime? Timestamp { get; set; }
}

public class TwiistInsulinDose
{
    [JsonPropertyName("identifier")]
    public string? Identifier { get; set; }

    [JsonPropertyName("doseType")]
    public string? DoseType { get; set; }

    [JsonPropertyName("startDate")]
    public DateTime? StartDate { get; set; }

    [JsonPropertyName("endDate")]
    public DateTime? EndDate { get; set; }

    [JsonPropertyName("value")]
    public decimal? Value { get; set; }

    [JsonPropertyName("valueUnit")]
    public string? ValueUnit { get; set; }
}

public class TwiistMeal
{
    [JsonPropertyName("addedDate")]
    public DateTime? AddedDate { get; set; }

    [JsonPropertyName("startDate")]
    public DateTime? StartDate { get; set; }

    [JsonPropertyName("absorptionTimeSeconds")]
    public decimal? AbsorptionTimeSeconds { get; set; }

    [JsonPropertyName("foodType")]
    public string? FoodType { get; set; }

    [JsonPropertyName("grams")]
    public decimal? Grams { get; set; }
}
