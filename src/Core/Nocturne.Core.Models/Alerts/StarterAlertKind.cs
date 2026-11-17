using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// One of the alert rules the setup hub's Alerts item starts a tenant with. Each is an ordinary
/// alert rule; the kind only lets the guided page find its own rules again.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StarterAlertKind>))]
public enum StarterAlertKind
{
    UrgentLow,
    Low,
    High,
    NoReadings,
}
