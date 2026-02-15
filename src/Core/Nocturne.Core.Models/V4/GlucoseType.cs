using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Source type of glucose reading
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GlucoseType>))]
public enum GlucoseType
{
    Finger,
    Sensor
}
