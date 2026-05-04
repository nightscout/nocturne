using Nocturne.API.Services.Alerts.Providers;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;

namespace Nocturne.API.Services.NotificationActionHandlers;

/// <summary>
/// Handles user actions on <c>alert.firing</c> in-app notifications produced by
/// <see cref="InAppProvider"/>. <c>ack</c> calls
/// <see cref="IAlertAcknowledgementService.AcknowledgeExcursionAsync"/> for the underlying
/// excursion (sourceId), then signals archive-as-completed. <c>dismiss</c> archives only — it
/// does not silence the alert; the next escalation step's delivery creates a fresh
/// notification (CreateNotificationAsync does not dedupe by sourceId).
/// </summary>
/// <remarks>
/// Authorisation: <see cref="Notifications.IInAppNotificationService.ExecuteActionAsync"/>
/// verifies <c>notification.UserId == userId</c> before dispatching here, so a forwarded
/// notificationId from another user is rejected upstream. Tenant scope for the ack call
/// comes from the request-scoped <see cref="ITenantAccessor"/>, never from the
/// notification payload.
/// </remarks>
/// <seealso cref="INotificationActionHandler"/>
/// <seealso cref="InAppProvider"/>
internal sealed class AlertActionHandler(
    IAlertAcknowledgementService acknowledgementService,
    ITenantAccessor tenantAccessor,
    ILogger<AlertActionHandler> logger
) : INotificationActionHandler
{
    public string NotificationType => InAppProvider.NotificationType;

    public async Task<NotificationActionResult> HandleAsync(
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
            return NotificationActionResult.NotHandled;
        }

        switch (actionId.ToLowerInvariant())
        {
            case InAppProvider.AckActionId:
                if (!tenantAccessor.IsResolved)
                {
                    logger.LogWarning(
                        "Cannot acknowledge excursion {ExcursionId} — no tenant context",
                        excursionId);
                    return NotificationActionResult.NotHandled;
                }

                await acknowledgementService.AcknowledgeExcursionAsync(
                    tenantAccessor.TenantId,
                    excursionId,
                    $"user:{userId}",
                    broadcast: true,
                    cancellationToken);

                return NotificationActionResult.Completed;

            case InAppProvider.DismissActionId:
                return NotificationActionResult.Dismissed;

            default:
                logger.LogWarning(
                    "Unknown action {ActionId} for alert.firing notification {NotificationId}",
                    actionId, notificationId);
                return NotificationActionResult.NotHandled;
        }
    }
}
