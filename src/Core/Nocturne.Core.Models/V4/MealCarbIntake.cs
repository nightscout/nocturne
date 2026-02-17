using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Meal response linking a carb intake with its correlated bolus and food attribution.
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public class MealCarbIntake
{
    [JsonPropertyName("carbIntake")]
    public CarbIntake CarbIntake { get; set; } = new();

    [JsonPropertyName("correlatedBolus")]
    public Bolus? CorrelatedBolus { get; set; }

    [JsonPropertyName("foods")]
    public List<TreatmentFood> Foods { get; set; } = [];

    [JsonPropertyName("isAttributed")]
    public bool IsAttributed { get; set; }

    [JsonPropertyName("attributedCarbs")]
    public decimal AttributedCarbs { get; set; }

    [JsonPropertyName("unspecifiedCarbs")]
    public decimal UnspecifiedCarbs { get; set; }
}
