using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Shared cleanup pathway invoked after an excursion closes (whether via the
/// orchestrator's per-reading state machine or the sweep service's periodic
/// tick): stamps <c>resolution_reason</c> on instances, expires their
/// pending deliveries, and broadcasts <c>alert_resolved</c>.
/// </summary>
/// <remarks>
/// The tracker is the single owner of <see cref="Nocturne.Core.Models.AlertTrackerState"/>
/// and the open/closed flag on the <see cref="Nocturne.Core.Models.AlertExcursion"/>
/// itself; this handler runs strictly after the close has been persisted.
/// </remarks>
public interface IExcursionResolutionHandler
{
    Task HandleClosedAsync(ExcursionTransition transition, Guid tenantId, CancellationToken ct);
}

internal sealed class ExcursionResolutionHandler(
    IAlertRepository repository,
    ISignalRBroadcastService broadcastService,
    IInAppNotificationService notificationService,
    TimeProvider timeProvider,
    ILogger<ExcursionResolutionHandler> logger)
    : IExcursionResolutionHandler
{
    public async Task HandleClosedAsync(ExcursionTransition transition, Guid tenantId, CancellationToken ct)
    {
        if (transition.Type != ExcursionTransitionType.ExcursionClosed) return;
        if (!transition.ExcursionId.HasValue) return;

        var excursionId = transition.ExcursionId.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var instances = await repository.GetInstancesForExcursionAsync(excursionId, ct);
        var instanceIds = instances.Select(i => i.Id).ToList();

        var reason = transition.CloseReason?.ToWireString();
        await repository.ResolveInstancesForExcursionAsync(excursionId, now, reason, ct);

        if (instanceIds.Count > 0)
        {
            await repository.ExpirePendingDeliveriesAsync(instanceIds, ct);
        }

        try
        {
            await broadcastService.BroadcastAlertEventAsync("alert_resolved", new
            {
                excursionId,
                tenantId,
                resolvedAt = now,
                reason,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast alert_resolved for excursion {ExcursionId}", excursionId);
        }

        await ArchiveInAppNotificationsAsync(excursionId, ct);

        logger.LogInformation(
            "Excursion {ExcursionId} resolved (reason={Reason}), {Count} instances closed",
            excursionId, reason ?? "(unspecified)", instances.Count);
    }

    /// <summary>
    /// Archives any in-app notifications created by <see cref="InAppProvider"/> for this
    /// excursion, so the bell/toast row clears once the alert is no longer firing.
    /// Iterates the unique InApp recipient userIds — one notification can exist per user.
    /// </summary>
    private async Task ArchiveInAppNotificationsAsync(Guid excursionId, CancellationToken ct)
    {
        IReadOnlyList<string> destinations;
        try
        {
            destinations = await repository.GetInAppDestinationsForExcursionAsync(excursionId, ct);
        }
        catch (Exception ex)
        {
            // Bumped to Error: a query-shape failure here would leak toast notifications across
            // every close — needs to be visible. Per-recipient archive failures stay Warning.
            logger.LogError(ex,
                "Failed to load InApp destinations for excursion {ExcursionId}; skipping auto-archive",
                excursionId);
            return;
        }

        if (destinations.Count == 0) return;

        var sourceId = excursionId.ToString();
        foreach (var userId in destinations)
        {
            try
            {
                await notificationService.ArchiveBySourceAsync(
                    userId,
                    InAppProvider.NotificationType,
                    sourceId,
                    NotificationArchiveReason.ConditionMet,
                    ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to archive InApp notification for user {UserId}, excursion {ExcursionId}",
                    userId, excursionId);
            }
        }
    }
}
