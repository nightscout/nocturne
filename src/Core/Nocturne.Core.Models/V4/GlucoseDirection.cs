using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Direction of glucose change based on CGM arrow display.
/// </summary>
/// <remarks>
/// Ordinal values map 1:1 to <see cref="GlucoseTrend"/> for numeric trend conversion.
/// Used by <see cref="SensorGlucose.Direction"/>; the computed <see cref="SensorGlucose.Trend"/>
/// property casts this enum to <see cref="GlucoseTrend"/>.
/// </remarks>
/// <seealso cref="GlucoseTrend"/>
/// <seealso cref="SensorGlucose"/>
[JsonConverter(typeof(JsonStringEnumConverter<GlucoseDirection>))]
public enum GlucoseDirection
{
    /// <summary>No direction available.</summary>
    None,

    /// <summary>Glucose rising rapidly (more than +3 mg/dL/min).</summary>
    DoubleUp,

    /// <summary>Glucose rising (+2 to +3 mg/dL/min).</summary>
    SingleUp,

    /// <summary>Glucose rising slightly (+1 to +2 mg/dL/min).</summary>
    FortyFiveUp,

    /// <summary>Glucose stable (-1 to +1 mg/dL/min).</summary>
    Flat,

    /// <summary>Glucose falling slightly (-1 to -2 mg/dL/min).</summary>
    FortyFiveDown,

    /// <summary>Glucose falling (-2 to -3 mg/dL/min).</summary>
    SingleDown,

    /// <summary>Glucose falling rapidly (more than -3 mg/dL/min).</summary>
    DoubleDown,

    /// <summary>Direction cannot be computed from available data.</summary>
    NotComputable,

    /// <summary>Rate of change is outside the computable range.</summary>
    RateOutOfRange
}

/// <summary>
/// Bridges <see cref="GlucoseDirection"/> to the legacy <see cref="Direction"/>, which owns the
/// Nightscout wire spellings.
/// </summary>
public static class GlucoseDirectionExtensions
{
    /// <summary>
    /// Gets the equivalent legacy <see cref="Direction"/>. <see cref="GlucoseDirection"/> is a subset:
    /// it has no triple arrows and no CGM-error value.
    /// </summary>
    public static Direction ToDirection(this GlucoseDirection direction) =>
        direction switch
        {
            GlucoseDirection.None => Direction.NONE,
            GlucoseDirection.DoubleUp => Direction.DoubleUp,
            GlucoseDirection.SingleUp => Direction.SingleUp,
            GlucoseDirection.FortyFiveUp => Direction.FortyFiveUp,
            GlucoseDirection.Flat => Direction.Flat,
            GlucoseDirection.FortyFiveDown => Direction.FortyFiveDown,
            GlucoseDirection.SingleDown => Direction.SingleDown,
            GlucoseDirection.DoubleDown => Direction.DoubleDown,
            GlucoseDirection.NotComputable => Direction.NotComputable,
            GlucoseDirection.RateOutOfRange => Direction.RateOutOfRange,
            _ => Direction.NONE,
        };

    /// <summary>
    /// Gets the equivalent <see cref="GlucoseDirection"/>, or <see langword="null"/> for the legacy
    /// values this enum does not model.
    /// </summary>
    public static GlucoseDirection? ToGlucoseDirection(this Direction direction) =>
        direction switch
        {
            Direction.NONE => GlucoseDirection.None,
            Direction.DoubleUp => GlucoseDirection.DoubleUp,
            Direction.SingleUp => GlucoseDirection.SingleUp,
            Direction.FortyFiveUp => GlucoseDirection.FortyFiveUp,
            Direction.Flat => GlucoseDirection.Flat,
            Direction.FortyFiveDown => GlucoseDirection.FortyFiveDown,
            Direction.SingleDown => GlucoseDirection.SingleDown,
            Direction.DoubleDown => GlucoseDirection.DoubleDown,
            Direction.NotComputable => GlucoseDirection.NotComputable,
            Direction.RateOutOfRange => GlucoseDirection.RateOutOfRange,
            _ => null,
        };

    /// <summary>
    /// Gets the legacy Nightscout wire spelling — see
    /// <see cref="DirectionExtensions.ToWireString(Direction)"/>. Required wherever a V4 direction is
    /// projected onto the v1/v2/v3 surface, where <c>ToString()</c> would emit an unrecognised
    /// PascalCase name.
    /// </summary>
    public static string ToWireString(this GlucoseDirection direction) =>
        direction.ToDirection().ToWireString();
}
