using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// System event severity/urgency type
/// </summary>
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

    /// <summary>
    /// Informational event
    /// </summary>
    Info
}

/// <summary>
/// System event device category
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SystemEventCategory>))]
public enum SystemEventCategory
{
    /// <summary>
    /// Pump-related events (alarms, errors)
    /// </summary>
    Pump,

    /// <summary>
    /// CGM-related events
    /// </summary>
    Cgm,

    /// <summary>
    /// Connectivity-related events
    /// </summary>
    Connectivity
}
