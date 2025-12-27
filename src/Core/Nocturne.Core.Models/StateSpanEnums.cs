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
    Profile
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
