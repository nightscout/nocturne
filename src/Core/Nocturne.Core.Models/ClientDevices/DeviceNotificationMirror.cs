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

    /// <summary>
    /// Whether an in-app notification should mirror to devices: never an alert (those go via
    /// device_action), never one whose title or subtitle is a label key (see <see cref="IsLabelKey"/>),
    /// and only when it's worth interrupting: urgency <see cref="NotificationUrgency.Warn"/>+
    /// or an <see cref="NotificationCategory.Alert"/>/<see cref="NotificationCategory.ActionRequired"/> category.
    /// Bare informational notifications are not mirrored.
    /// </summary>
    public static bool Qualifies(InAppNotificationDto notification)
        => notification.Type != InAppNotificationType.AlertFiring.ToWireString()
           && !IsLabelKey(notification.Title)
           && !IsLabelKey(notification.Subtitle)
           && (notification.Urgency >= NotificationUrgency.Warn
               || notification.Category is NotificationCategory.Alert or NotificationCategory.ActionRequired);

    /// <summary>
    /// Whether <paramref name="text"/> is a label key: lowercase letters, digits and underscores
    /// only, starting with a letter. The desktop app has no translations, so it must not show one.
    /// Text of the same shape (one lowercase word, snake_case) is skipped as well, on purpose: a skipped
    /// toast still shows in the web app, and a leaked key does not read.
    /// </summary>
    public static bool IsLabelKey(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[0] is < 'a' or > 'z')
            return false;

        foreach (var c in text)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
                return false;
        }

        return true;
    }
}
