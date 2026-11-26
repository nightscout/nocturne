using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Sends a recorded notice about a rule (<see cref="IUnevaluableRuleNotice"/>,
/// <see cref="IHeldRuleNotice"/>) through the rule's own channels at its severity, behind the Do Not
/// Disturb gate its excursions pass (<see cref="DndGatedDispatch"/>), runs the notice's resolution
/// again when it ended during the dispatch (<see cref="IExcursionResolutionHandler.CloseAgainIfEndedAsync"/>),
/// then settles the work its row owed. A channel already answered is not sent again
/// (<see cref="IAlertDeliveryService.DispatchAsync"/>).
/// </summary>
internal static class RuleNoticeDispatch
{
    /// <summary>What a notice needs of a rule, which an unreadable rule has less of.</summary>
    public sealed record NoticeRule(
        Guid Id,
        string Name,
        AlertConditionType? ConditionType,
        AlertRuleSeverity Severity,
        bool AllowThroughDnd,
        RuleScopeClass ScopeClass)
    {
        public static NoticeRule Of(AlertRuleSnapshot rule) =>
            new(rule.Id, rule.Name, rule.ConditionType, rule.Severity, rule.AllowThroughDnd, rule.ScopeClass);
    }

    /// <summary>
    /// Dispatches <paramref name="notice"/>, whose payload is built from the tenant's alert context
    /// and its count of open excursions.
    /// </summary>
    public static async Task DispatchAsync(
        NoticeRule rule,
        NoticeRecorded notice,
        Func<TenantAlertContext?, int, AlertPayload> payload,
        Guid tenantId,
        IAlertRepository repository,
        IAlertRuleRepository ruleRepository,
        IAlertDeliveryService deliveryService,
        ISensorContextEnricher contextEnricher,
        IExcursionResolutionHandler resolutionHandler,
        ILogger logger,
        CancellationToken ct)
    {
        var channels = (await ruleRepository.GetChannelsAsync([rule.Id], ct)).GetValueOrDefault(rule.Id) ?? [];
        var activeExcursionCount = await repository.CountActiveExcursionsAsync(ct);
        var tenant = await repository.GetTenantAlertContextAsync(tenantId, ct);

        var dnd = await contextEnricher.EnrichAsync(
            new SensorContext { LatestValue = null, LatestTimestamp = null, TrendRate = null, LastReadingAt = null }, [], ct);
        await DndGatedDispatch.DispatchAsync(
            rule.Name, rule.Severity, rule.AllowThroughDnd, rule.ScopeClass, dnd.ActiveDndScopes, notice.InstanceId,
            channels, payload(tenant, activeExcursionCount), repository, deliveryService, logger, ct);
        await resolutionHandler.CloseAgainIfEndedAsync(notice.ExcursionId, tenantId, ct);
        await repository.SettleTransitionAsync(notice.ExcursionId, ExcursionTransitionType.ExcursionOpened, ct);
    }

    /// <summary>
    /// Resolves a standing notice of either kind: ends it, then runs its close
    /// (<see cref="IExcursionResolutionHandler.HandleClosedAsync"/>) with <paramref name="reason"/>.
    /// </summary>
    public static async Task ResolveAsync(
        Guid excursionId,
        ExcursionCloseReason reason,
        Guid tenantId,
        IAlertRepository repository,
        IExcursionResolutionHandler resolutionHandler,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        await repository.EndNoticeAsync(excursionId, timeProvider.GetUtcNow().UtcDateTime, ct);
        await resolutionHandler.HandleClosedAsync(
            new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, excursionId, reason), tenantId, ct);
    }
}
