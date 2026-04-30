using Nocturne.API.Services.Alerts.Providers;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.NotificationActionHandlers;

/// <summary>
/// Handles user actions on <c>alert.firing</c> in-app notifications produced by
/// <see cref="InAppProvider"/>. <c>ack</c> calls
/// <see cref="IAlertAcknowledgementService.AcknowledgeExcursionAsync"/> for the underlying
/// excursion (sourceId), then archives the notification. <c>dismiss</c> archives only — it
/// does not silence the alert; another delivery for the same excursion will create a fresh
/// notification.
/// </summary>
/// <seealso cref="INotificationActionHandler"/>
/// <seealso cref="InAppProvider"/>
public class AlertActionHandler(
    IAlertAcknowledgementService acknowledgementService,
    IInAppNotificationService notificationService,
    ITenantAccessor tenantAccessor,
    ILogger<AlertActionHandler> logger
) : INotificationActionHandler
{
    public string NotificationType => InAppProvider.NotificationType;

    public async Task<bool> HandleAsync(
        Guid notificationId,
        string actionId,
        string userId,
        string? sourceId,
        Dictionary<string, object>? metadata,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(sourceId, out var excursionId))
        {
            logger.LogWarning(
                "Notification {NotificationId} has invalid sourceId '{SourceId}'; cannot resolve excursion",
                notificationId, sourceId);
            return false;
        }

        switch (actionId.ToLowerInvariant())
        {
            case InAppProvider.AckActionId:
                if (!tenantAccessor.IsResolved)
                {
                    logger.LogWarning(
                        "Cannot acknowledge excursion {ExcursionId} — no tenant context",
                        excursionId);
                    return false;
                }

                await acknowledgementService.AcknowledgeExcursionAsync(
                    tenantAccessor.TenantId,
                    excursionId,
                    $"user:{userId}",
                    broadcast: true,
                    cancellationToken);

                await notificationService.ArchiveNotificationAsync(
                    notificationId, NotificationArchiveReason.Completed, cancellationToken);
                return true;

            case InAppProvider.DismissActionId:
                await notificationService.ArchiveNotificationAsync(
                    notificationId, NotificationArchiveReason.Dismissed, cancellationToken);
                return true;

            default:
                logger.LogWarning(
                    "Unknown action {ActionId} for alert.firing notification {NotificationId}",
                    actionId, notificationId);
                return false;
        }
    }
}
