using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Eversense.Models;

/// <summary>
/// One row of the care API's user-event feeds. <see cref="EventTypeId"/> says what the row is
/// (<see cref="Configurations.EversenseConstants.SensorGlucoseEventType"/> for sensor glucose);
/// <see cref="Value"/> is in mg/dL for glucose rows and <see cref="EventDate"/> is GMT without a zone.
/// </summary>
public class EversenseUserEvent
{
    [JsonPropertyName("EventTypeID")]
    public int EventTypeId { get; set; }

    [JsonPropertyName("EventSubTypeID")]
    public int EventSubTypeId { get; set; }

    [JsonPropertyName("Value")]
    public int Value { get; set; }

    [JsonPropertyName("EventDate")]
    public string EventDate { get; set; } = string.Empty;
}
