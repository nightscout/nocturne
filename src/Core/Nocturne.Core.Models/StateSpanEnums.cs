using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Category of state span for grouping and UI filtering
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StateSpanCategory>))]
public enum StateSpanCategory
{
    /// <summary>
    /// Pump operational mode (Automatic, Manual, Boost, etc.)
    /// </summary>
    PumpMode,

    /// <summary>
    /// Pump connectivity status
    /// </summary>
    PumpConnectivity,

    /// <summary>
    /// Active override configuration
    /// </summary>
    Override,

    /// <summary>
    /// Active basal profile
    /// </summary>
    Profile,

    /// <summary>
    /// Pump-confirmed basal delivery rate.
    /// Use metadata.origin to distinguish between Algorithm, Scheduled, Manual, and Suspended.
    /// </summary>
    BasalDelivery,

    /// <summary>
    /// User-annotated sleep period
    /// </summary>
    Sleep,

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
    DataExclusion
}

/// <summary>
/// Pump operational mode states
/// </summary>
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

    /// <summary>
    /// Exercise/activity mode
    /// </summary>
    Exercise,

    /// <summary>
    /// Insulin delivery suspended
    /// </summary>
    Suspended,

    /// <summary>
    /// Pump powered off
    /// </summary>
    Off
}

/// <summary>
/// Pump connectivity states
/// </summary>
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

    /// <summary>
    /// Bluetooth disabled
    /// </summary>
    BluetoothOff
}

/// <summary>
/// Override states
/// </summary>
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

/// <summary>
/// Profile states
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProfileState>))]
public enum ProfileState
{
    /// <summary>
    /// A profile is active (name in metadata)
    /// </summary>
    Active
}

/// <summary>
/// Basal delivery states
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BasalDeliveryState>))]
public enum BasalDeliveryState
{
    /// <summary>
    /// Basal rate is being delivered
    /// </summary>
    Active
}

/// <summary>
/// What initiated the basal delivery rate
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BasalDeliveryOrigin>))]
public enum BasalDeliveryOrigin
{
    /// <summary>
    /// Closed-loop algorithm adjusted (CamAPS, Control-IQ, Loop)
    /// </summary>
    Algorithm,

    /// <summary>
    /// Pump's programmed basal schedule
    /// </summary>
    Scheduled,

    /// <summary>
    /// User-initiated temporary rate
    /// </summary>
    Manual,

    /// <summary>
    /// Delivery suspended (rate = 0)
    /// </summary>
    Suspended,

    /// <summary>
    /// Derived from profile when no pump data available
    /// </summary>
    Inferred
}
