using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// How the Halo Dial colors the glucose ring: discrete bucket colors
/// (matching the rest of the dashboard) or a continuous oklch interpolation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HaloDialColorMode
{
    Discrete,
    Continuous,
}

/// <summary>
/// Which prediction curve the dial draws when multiple are available
/// (Main / IOB-only / UAM / COB / ZeroTemp).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HaloDialPredictionCurve
{
    Main,
    Iob,
    Uam,
    Cob,
    ZeroTemp,
}

/// <summary>
/// Closed catalogue of elements that may appear in a corner slot of the Halo Dial.
/// Each element type is unique across the dial; adding new elements requires a code change.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HaloDialCornerElement
{
    BasalRate,
    Reservoir,
    SensorAge,
    PumpSiteAge,
    Battery,
    LoopLabel,
    LoopDot,
    Direction,
    Eventual,
}

/// <summary>
/// Closed catalogue of elements that may appear in the inner-arc slots of the Halo Dial.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HaloDialArcElement
{
    Iob,
    Cob,
    BasalPercent,
    Sensitivity,
}

/// <summary>
/// What the dial renders below the centre BG number.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HaloDialCenterSubElement
{
    MinutesAndDelta,
    MinutesOnly,
    DeltaOnly,
    Mmol,
    None,
}
