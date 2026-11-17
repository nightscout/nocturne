using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Numeric trend value corresponding to CGM trend arrows.
/// </summary>
/// <remarks>
/// Integer values map 1:1 to <see cref="GlucoseDirection"/>. The computed property
/// <see cref="SensorGlucose.Trend"/> casts a <see cref="GlucoseDirection"/> value to
/// this enum by ordinal.
/// </remarks>
/// <seealso cref="GlucoseDirection"/>
/// <seealso cref="SensorGlucose"/>
[JsonConverter(typeof(JsonStringEnumConverter<GlucoseTrend>))]
public enum GlucoseTrend
{
    /// <summary>No trend available.</summary>
    None = 0,

    /// <summary>Rapidly rising.</summary>
    DoubleUp = 1,

    /// <summary>Rising.</summary>
    SingleUp = 2,

    /// <summary>Rising slightly.</summary>
    FortyFiveUp = 3,

    /// <summary>Stable.</summary>
    Flat = 4,

    /// <summary>Falling slightly.</summary>
    FortyFiveDown = 5,

    /// <summary>Falling.</summary>
    SingleDown = 6,

    /// <summary>Rapidly falling.</summary>
    DoubleDown = 7,

    /// <summary>Not computable.</summary>
    NotComputable = 8,

    RateOutOfRange = 9
}
