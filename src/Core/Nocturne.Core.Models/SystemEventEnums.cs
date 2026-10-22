using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// System event severity/urgency type for <see cref="SystemEvent"/> records.
/// </summary>
/// <seealso cref="SystemEvent"/>
/// <seealso cref="SystemEventCategory"/>
[JsonConverter(typeof(JsonStringEnumConverter<SystemEventType>))]
public enum SystemEventType
{
    /// <summary>
    /// Critical event requiring immediate attention
    /// </summary>
    Alarm,

    /// <summary>
    /// Important event requiring attention
    /// </summary>
    Hazard,

    /// <summary>
    /// Advisory event that may require attention
    /// </summary>
    Warning,

    Info
}

/// <summary>
/// System event device category for <see cref="SystemEvent"/> records.
/// Groups events by the device type that generated them.
/// </summary>
/// <seealso cref="SystemEvent"/>
/// <seealso cref="SystemEventType"/>
[JsonConverter(typeof(JsonStringEnumConverter<SystemEventCategory>))]
public enum SystemEventCategory
{
    /// <summary>
    /// Pump-related events (alarms, errors)
    /// </summary>
    Pump,

    Cgm,

    Connectivity
}
