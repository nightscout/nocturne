using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
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

        logger.LogInformation(
            "Excursion {ExcursionId} resolved (reason={Reason}), {Count} instances closed",
            excursionId, reason ?? "(unspecified)", instances.Count);
    }
}
