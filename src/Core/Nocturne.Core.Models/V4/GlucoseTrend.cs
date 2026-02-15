using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Numeric trend value corresponding to CGM trend arrows
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GlucoseTrend>))]
public enum GlucoseTrend
{
    None = 0,
    DoubleUp = 1,
    SingleUp = 2,
    FortyFiveUp = 3,
    Flat = 4,
    FortyFiveDown = 5,
    SingleDown = 6,
    DoubleDown = 7,
    NotComputable = 8,
    RateOutOfRange = 9
}
