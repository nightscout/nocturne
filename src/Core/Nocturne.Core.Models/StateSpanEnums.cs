using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Category of state span for grouping and UI filtering.
/// Used by <see cref="StateSpan.Category"/> to classify the nature of the time period.
/// </summary>
/// <seealso cref="StateSpan"/>
[JsonConverter(typeof(JsonStringEnumConverter<StateSpanCategory>))]
public enum StateSpanCategory
{
    /// <summary>
    /// Pump operational mode (Automatic, Manual, Boost, etc.)
    /// </summary>
    PumpMode,

    PumpConnectivity,

    Override,

    /// <summary>
    /// Active basal profile
    /// </summary>
    Profile,

    /// <summary>
    /// User-annotated exercise/activity period
    /// </summary>
    Exercise,

    /// <summary>
    /// User-annotated illness period (affects insulin sensitivity)
    /// </summary>
    Illness,

    /// <summary>
    /// User-annotated travel/timezone change period
    /// </summary>
    Travel,

    /// <summary>
    /// Data exclusion period (compression lows, sensor errors)
    /// </summary>
    DataExclusion,

    /// <summary>
    /// AAPS temporary glucose target (Eating Soon, Activity, Manual)
    /// </summary>
    TemporaryTarget,

    /// <summary>
    /// Detected motion class from xDrip (walking, still, in vehicle), held until the next one
    /// </summary>
    Motion
}

[JsonConverter(typeof(JsonStringEnumConverter<PumpModeState>))]
public enum PumpModeState
{
    /// <summary>
    /// Closed-loop, full automation
    /// </summary>
    Automatic,

    /// <summary>
    /// Partial automation (e.g., Op5 Limited)
    /// </summary>
    Limited,

    /// <summary>
    /// Open-loop, no automation
    /// </summary>
    Manual,

    /// <summary>
    /// Aggressive insulin delivery
    /// </summary>
    Boost,

    /// <summary>
    /// Reduced insulin delivery
    /// </summary>
    EaseOff,

    /// <summary>
    /// Sleep mode (Control-IQ)
    /// </summary>
    Sleep,

    Exercise,

    /// <summary>
    /// Liberty mode (CamAPS FX)
    /// </summary>
    Liberty,

    /// <summary>
    /// Insulin delivery suspended
    /// </summary>
    Suspended,

    Off
}

[JsonConverter(typeof(JsonStringEnumConverter<PumpConnectivityState>))]
public enum PumpConnectivityState
{
    /// <summary>
    /// Normal communication
    /// </summary>
    Connected,

    /// <summary>
    /// Lost connection (unintentional)
    /// </summary>
    Disconnected,

    /// <summary>
    /// Intentionally disconnected
    /// </summary>
    Removed,

    BluetoothOff
}

[JsonConverter(typeof(JsonStringEnumConverter<OverrideState>))]
public enum OverrideState
{
    /// <summary>
    /// No override active
    /// </summary>
    None,

    /// <summary>
    /// User-defined override (details in metadata)
    /// </summary>
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter<TemporaryTargetState>))]
public enum TemporaryTargetState
{
    Active,

    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter<ProfileState>))]
public enum ProfileState
{
    /// <summary>
    /// A profile is active (name in metadata)
    /// </summary>
    Active
}

