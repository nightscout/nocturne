using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts.Conditions;

/// <summary>
/// Sensor-state alert condition: the sensor's state is one of <see cref="States"/>, and with
/// <see cref="ForMinutes"/> has been for at least that long. Unknown while no state is known.
/// </summary>
public record SensorStateCondition(
    List<SensorState> States,
    [property: JsonPropertyName("for_minutes")] int? ForMinutes);
