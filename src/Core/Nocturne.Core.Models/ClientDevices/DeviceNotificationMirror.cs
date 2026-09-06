namespace Nocturne.Core.Models.ClientDevices;

/// <summary>
/// A non-alert in-app notification mirrored to a subject's registered devices. Alerts reach devices
/// via <c>device_action</c> channels (rich, capability-driven), never this path — so <c>alert.firing</c>
/// is excluded here. This carries the ambient stream (meal-match, membership, review nudges, …).
/// </summary>
/// <remarks>
/// Broadcast tenant-wide (like the in-app notification stream already is); the owning
/// <see cref="UserId"/> is included so a device mirrors only its own owner's notifications.
/// </remarks>
public class DeviceNotificationMirror
{
    /// <summary>The subject the notification belongs to — a device filters to its own owner.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The notification to surface on the device.</summary>
    public InAppNotificationDto Notification { get; set; } = new();

    /// <summary>The in-app type that the alert engine uses; excluded from the mirror (alerts use device_action).</summary>
    public const string AlertFiringType = "alert.firing";

    /// <summary>
    /// Whether an in-app notification should mirror to devices: never an alert (those go via
    /// device_action), and only when it's worth interrupting — urgency <see cref="NotificationUrgency.Warn"/>+
    /// or an <see cref="NotificationCategory.Alert"/>/<see cref="NotificationCategory.ActionRequired"/> category.
    /// Bare informational notifications are not mirrored.
    /// </summary>
    public static bool Qualifies(InAppNotificationDto notification)
        => notification.Type != AlertFiringType
           && (notification.Urgency >= NotificationUrgency.Warn
               || notification.Category is NotificationCategory.Alert or NotificationCategory.ActionRequired);
}
