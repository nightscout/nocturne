using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Direction of glucose change based on CGM arrow display
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GlucoseDirection>))]
public enum GlucoseDirection
{
    None,
    DoubleUp,
    SingleUp,
    FortyFiveUp,
    Flat,
    FortyFiveDown,
    SingleDown,
    DoubleDown,
    NotComputable,
    RateOutOfRange
}
