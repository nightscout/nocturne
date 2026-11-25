using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Shared cleanup pathway invoked after an excursion closes (whether via the
/// orchestrator's per-reading state machine or <see cref="AlertSweepJob"/>'s
/// periodic run): stamps <c>resolution_reason</c> on instances, expires their
/// pending deliveries, deletes members' mutes of the excursion, broadcasts
/// <c>alert_resolved</c>, archives the in-app notifications and clears the alarm, then records
/// that the close owes no more work (<see cref="IAlertRepository.SettleTransitionAsync"/>).
/// </summary>
/// <remarks>
/// The tracker is the single owner of <see cref="Nocturne.Core.Models.AlertTrackerState"/>
/// and the open/closed flag on the <see cref="Nocturne.Core.Models.AlertExcursion"/>
/// itself; this handler runs strictly after the close has been persisted. Every step is safe to
/// run again, which is how <see cref="AlertSweepJob"/> finishes a close a run that died left
/// unsettled; that run passes no close reason, since none is stored, so an instance it resolves
/// has none.
/// </remarks>
public interface IExcursionResolutionHandler
{
    Task HandleClosedAsync(ExcursionTransition transition, Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Runs the close of <paramref name="excursionId"/> once more when the excursion has ended,
    /// for what an opening's sends raised after the close ran: the instance lock orders the
    /// instance against a close, not the sends. The close is owed again first
    /// (<see cref="IAlertRepository.OweCloseIfEndedAsync"/>), so a run that dies is finished by the
    /// sweep. False, with nothing run, while the excursion is open.
    /// </summary>
    Task<bool> CloseAgainIfEndedAsync(Guid excursionId, Guid tenantId, CancellationToken ct);
}

internal sealed class ExcursionResolutionHandler(
    IAlertRepository repository,
    ISignalRBroadcastService broadcastService,
    IInAppNotificationService notificationService,
    IEngineAlarmRelay alarmRelay,
    TimeProvider timeProvider,
    ILogger<ExcursionResolutionHandler> logger)
    : IExcursionResolutionHandler
{
    public async Task<bool> CloseAgainIfEndedAsync(Guid excursionId, Guid tenantId, CancellationToken ct)
    {
        if (!await repository.OweCloseIfEndedAsync(excursionId, timeProvider.GetUtcNow().UtcDateTime, ct))
            return false;

        logger.LogInformation(
            "Excursion {ExcursionId} has ended; its close runs again for what its opening sent", excursionId);
        await HandleClosedAsync(
            new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, excursionId), tenantId, ct);
        return true;
    }

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

        await repository.DeleteExcursionMutesAsync(excursionId, ct);

        ExcursionRuleSnapshot? rule = null;
        try
        {
            rule = await repository.GetExcursionRuleAsync(excursionId, ct);
            if (rule is null)
            {
                // Only an excursion deleted with its rule (an FK cascade) has none.
                logger.LogWarning(
                    "Excursion {ExcursionId} has no rule; alert_resolved not broadcast", excursionId);
            }
            else
            {
                var resolved = new AlertResolvedEvent
                {
                    TenantId = tenantId,
                    ExcursionId = excursionId,
                    AlertRuleId = rule.AlertRuleId,
                    RuleName = rule.Name,
                    ResolvedAt = now,
                    Reason = transition.CloseReason,
                };
                // A notice reached only its rule's channels, so only a Home Assistant instance it
                // was delivered to hears that it is resolved.
                if (rule.IsNotice)
                    await broadcastService.BroadcastAlertResolvedToHomeAssistantAsync(
                        resolved, await repository.GetHomeAssistantInstancesSentExcursionAsync(excursionId, ct));
                else
                    await broadcastService.BroadcastAlertResolvedAsync(resolved);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to broadcast alert_resolved for excursion {ExcursionId}", excursionId);
        }

        // A rule that could not be read leaves the kind of excursion unknown, so both kinds of
        // notification it may have made are archived.
        IReadOnlyList<string> notificationTypes = rule is null
            ? InAppProvider.AllNotificationTypes
            : [InAppProvider.NotificationTypeFor(rule)];
        await ArchiveInAppNotificationsAsync(excursionId, notificationTypes, ct);
        await alarmRelay.ClearIfQuietAsync([excursionId], ct);
        await repository.SettleTransitionAsync(excursionId, ExcursionTransitionType.ExcursionClosed, ct);

        logger.LogInformation(
            "Excursion {ExcursionId} resolved (reason={Reason}), {Count} instances closed",
            excursionId, reason ?? "(unspecified)", instances.Count);
    }

    /// <summary>
    /// Archives any in-app notifications created by <see cref="InAppProvider"/> for this
    /// excursion, so the bell/toast row clears once the alert is no longer firing.
    /// Iterates the unique InApp recipient userIds — one notification can exist per user.
    /// </summary>
    private async Task ArchiveInAppNotificationsAsync(
        Guid excursionId, IReadOnlyList<string> notificationTypes, CancellationToken ct)
    {
        IReadOnlyList<string> destinations;
        try
        {
            destinations = await repository.GetInAppDestinationsForExcursionAsync(excursionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
        foreach (var (userId, notificationType) in destinations.SelectMany(u => notificationTypes.Select(t => (u, t))))
        {
            try
            {
                await notificationService.ArchiveBySourceAsync(
                    userId,
                    notificationType,
                    sourceId,
                    NotificationArchiveReason.ConditionMet,
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex,
                    "Failed to archive InApp notification for user {UserId}, excursion {ExcursionId}",
                    userId, excursionId);
            }
        }
    }
}
