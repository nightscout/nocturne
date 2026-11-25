using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The work an excursion's opening owes (docs/alerts/engine-semantics.md §7): its instance, its
/// dispatch behind Do Not Disturb, and the auto-acknowledge of an info rule; then the record that
/// the work is done (<see cref="IAlertRepository.SettleTransitionAsync"/>).
/// </summary>
/// <remarks>
/// Safe to run again for an opening a run that died left unsettled, which is how
/// <see cref="AlertSweepJob"/> re-dispatches it: the instance is the one the first run raised
/// (<see cref="IAlertRepository.EnsureInstanceAsync"/>), an instance that run silenced stays silent
/// and is not dispatched, the dispatch sends only the channels still owed
/// (<see cref="IAlertDeliveryService.DispatchAsync"/>), and acknowledging an acknowledged excursion
/// changes nothing. So a re-run of an opening whose dispatch and info acknowledgement both finished
/// sends nothing, acknowledges nothing new, and settles. The payload is built from the context as
/// it is at the run, so a re-dispatch carries the reading it found then, with that reading's time.
/// <para>
/// A close that lands while the sends run has resolved, archived and cleared only what existed
/// before them, so once they return an excursion found ended has its close run again
/// (<see cref="IExcursionResolutionHandler.CloseAgainIfEndedAsync"/>). A late opening that finds its
/// excursion ended does the same, since a run that died after its sends may have left them standing.
/// </para>
/// </remarks>
public interface IExcursionOpenedHandler
{
    /// <remarks>
    /// <c>metBy</c> is what the opening evaluation's true forecast and loop conditions met, which the
    /// payload names (<see cref="AlertPayload.MetBy"/>); a late opening has no evaluation and sends
    /// none. <c>late</c> is the sweep finishing an opening its evaluation left owed. A late opening whose
    /// excursion has ended by the time its instance is taken is settled without a send. The
    /// evaluation that opened the excursion sends it even when the same evaluation's auto-resolve
    /// has already closed it; <c>callerCloses</c> is that evaluation, whose close runs once this
    /// returns and so after the sends, and the close is then not run here as well.
    /// </remarks>
    Task HandleOpenedAsync(
        AlertRuleSnapshot rule, Guid excursionId, SensorContext context, Guid tenantId, bool late,
        IReadOnlyList<AlertMetPoint> metBy, bool callerCloses, CancellationToken ct);
}

internal sealed class ExcursionOpenedHandler(
    IAlertRepository repository,
    IAlertRuleRepository ruleRepository,
    IAlertDeliveryService deliveryService,
    IAlertAcknowledgementService acknowledgementService,
    IExcursionResolutionHandler resolutionHandler,
    TimeProvider timeProvider,
    ILogger<ExcursionOpenedHandler> logger)
    : IExcursionOpenedHandler
{
    public async Task HandleOpenedAsync(
        AlertRuleSnapshot rule, Guid excursionId, SensorContext context, Guid tenantId, bool late,
        IReadOnlyList<AlertMetPoint> metBy, bool callerCloses, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var instance = await repository.EnsureInstanceAsync(excursionId, now, whileOpen: late, ct);

        if (instance is null)
        {
            logger.LogWarning(
                "Excursion {ExcursionId} ended before its late dispatch raised an instance; it is not sent",
                excursionId);
            await resolutionHandler.CloseAgainIfEndedAsync(excursionId, tenantId, ct);
        }
        else if (instance.Suppressed)
        {
            logger.LogInformation(
                "Alert instance {InstanceId} for excursion {ExcursionId} was silenced by Do Not Disturb; not dispatched again",
                instance.Id, excursionId);
        }
        else
        {
            // An empty channel list is allowed: the user chose no delivery channels, and the
            // alert is still tracked.
            var channels = (await ruleRepository.GetChannelsAsync([rule.Id], ct)).GetValueOrDefault(rule.Id) ?? [];
            var activeExcursionCount = await repository.CountActiveExcursionsAsync(ct);
            var tenant = await repository.GetTenantAlertContextAsync(tenantId, ct);
            var payload = AlertPayloads.Build(
                rule, context, tenantId, excursionId, instance.Id, tenant, activeExcursionCount, now, metBy);

            var suppressingScope = await DndGatedDispatch.DispatchAsync(
                rule, context.ActiveDndScopes, instance.Id, channels, payload, repository, deliveryService, logger, ct);

            logger.LogInformation(
                "Alert instance {InstanceId} dispatched for excursion {ExcursionId}, rule {RuleName}",
                instance.Id, excursionId, rule.Name);

            // Info severity is fire-and-forget: deliver once, then acknowledge so the alert
            // renders as acknowledged. broadcast=false avoids racing the alert_acknowledged event
            // against the alert_dispatch just emitted. A silenced instance had no dispatch to
            // follow up, and an acknowledgement would race its suppression history row.
            if (rule.Severity == AlertRuleSeverity.Info && suppressingScope is null)
            {
                await acknowledgementService.AcknowledgeExcursionAsync(
                    tenantId, excursionId, "system:auto-ack-on-trigger", AlertAcknowledgementAuthority.System,
                    broadcast: false, ct);
            }

            if (!callerCloses)
                await resolutionHandler.CloseAgainIfEndedAsync(excursionId, tenantId, ct);
        }

        await repository.SettleTransitionAsync(excursionId, ExcursionTransitionType.ExcursionOpened, ct);
    }
}
