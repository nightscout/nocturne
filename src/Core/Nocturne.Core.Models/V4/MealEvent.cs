using System.Text.Json.Serialization;
using Nocturne.Core.Models;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Event-centric view of a meal: a group of correlated carb intakes and boluses
/// sharing a <see cref="CorrelationId"/>, plus any food attribution rows.
/// Orphan carb intakes (null CorrelationId) are still represented, one per event,
/// with an empty <see cref="Boluses"/> array.
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public class MealEvent
{
    /// <summary>
    /// Shared correlation identifier. Null for an orphan event (no correlation).
    /// </summary>
    [JsonPropertyName("correlationId")]
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Earliest timestamp across all carb intakes and boluses in the group.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// All <see cref="CarbIntake"/> records in this meal event group.
    /// </summary>
    [JsonPropertyName("carbIntakes")]
    public CarbIntake[] CarbIntakes { get; set; } = [];

    /// <summary>
    /// All <see cref="Bolus"/> records correlated with this meal event.
    /// </summary>
    [JsonPropertyName("boluses")]
    public Bolus[] Boluses { get; set; } = [];

    /// <summary>
    /// <see cref="TreatmentFood"/> attribution rows linking carbs to specific food items.
    /// </summary>
    [JsonPropertyName("foods")]
    public TreatmentFood[] Foods { get; set; } = [];

    /// <summary>
    /// Sum of all <see cref="CarbIntake.Carbs"/> in this meal event (grams).
    /// </summary>
    [JsonPropertyName("totalCarbs")]
    public double TotalCarbs { get; set; }

    /// <summary>
    /// Portion of <see cref="TotalCarbs"/> attributed to specific <see cref="TreatmentFood"/> items.
    /// </summary>
    [JsonPropertyName("attributedCarbs")]
    public double AttributedCarbs { get; set; }

    /// <summary>
    /// Portion of <see cref="TotalCarbs"/> not attributed to any specific food item.
    /// </summary>
    /// <remarks>
    /// Computed as <c>TotalCarbs - AttributedCarbs</c>.
    /// </remarks>
    [JsonPropertyName("unspecifiedCarbs")]
    public double UnspecifiedCarbs { get; set; }

    /// <summary>
    /// Sum of all <see cref="Bolus.Insulin"/> in this meal event (units).
    /// </summary>
    [JsonPropertyName("totalInsulin")]
    public double TotalInsulin { get; set; }

    /// <summary>
    /// Whether any <see cref="TreatmentFood"/> attributions exist for this meal event.
    /// </summary>
    [JsonPropertyName("isAttributed")]
    public bool IsAttributed { get; set; }
}
