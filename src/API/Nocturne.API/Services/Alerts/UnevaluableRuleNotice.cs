using System.Security.Cryptography;
using System.Text;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Tells a rule's recipients, once per version of its condition, that the rule cannot fire until
/// it is edited (docs/alerts/engine-semantics.md §1.4), and takes the notice back once it can.
/// </summary>
/// <remarks>
/// <para>
/// Every problem that keeps one of a rule's trees from being evaluated is logged once per version
/// of its condition, by whichever of <see cref="Report"/> and <see cref="CheckAsync"/> sees the
/// version first. Only a body that cannot be evaluated skips the rule, so only that is sent: an
/// auto-resolve tree or snooze conditions that cannot be evaluated leave the rule firing.
/// </para>
/// <para>
/// Only <see cref="AlertSweepJob"/> sends, as the system, so that no upload waits on a webhook
/// before its rules are evaluated. The notice goes through the rule's own channels at the rule's
/// severity, behind the same Do Not Disturb gate as its excursions (<see cref="DndGatedDispatch"/>).
/// Its row (<see cref="IAlertRepository.RecordUnevaluableNoticeAsync"/>), not
/// <see cref="ConditionVersionLog"/>, decides whether it is sent: each run reads the tenant's
/// standing notices and dispatches one that has not been dispatched, so a run that fails or is
/// cancelled between the row and the dispatch is made good by the next, and a restart or a second
/// instance does not send it again. Sending is at least once only up to the delivery rows: a
/// channel whose provider fails is recorded failed and not retried, as for an excursion. Two runs
/// for one tenant are kept apart by the job ledger's lease; a run that loses its lease while it
/// dispatches can send a notice twice, the safe way to fail.
/// </para>
/// <para>
/// A standing notice is resolved through <see cref="IExcursionResolutionHandler"/>, as a closed
/// excursion is, when the rule's condition can be evaluated again, when the condition changes to
/// another that cannot, and when the rule is disabled or deleted (<see cref="ResolveAsync"/>).
/// Each run is also the backstop for those: it resolves every standing notice whose rule is not
/// enabled or no longer has that broken version, whatever path missed it (a disable that raced the
/// run, a retirement that failed), so a rule enabled again while still broken is told again.
/// </para>
/// </remarks>
public interface IUnevaluableRuleNotice
{
    /// <summary>
    /// Logs the problems with each rule's conditions the first time this process sees the version,
    /// and does nothing else: no read, no write, no send. For evaluation, which must not wait.
    /// </summary>
    void Report(IReadOnlyList<AlertRuleSnapshot> rules);

    /// <summary>
    /// Sends the notice for each rule in <paramref name="enabledRules"/> (every enabled rule of the
    /// tenant) whose body cannot be evaluated, unless it was dispatched for this version, and
    /// resolves every other standing notice of the tenant. A failure for one notice is logged and
    /// never stops the others.
    /// </summary>
    Task CheckAsync(IReadOnlyList<AlertRuleSnapshot> enabledRules, CancellationToken ct);

    /// <summary>Resolves the rule's standing notice, for a rule being disabled or deleted.</summary>
    Task ResolveAsync(Guid ruleId, ExcursionCloseReason reason, CancellationToken ct);
}

internal sealed class UnevaluableRuleNotice(
    IAlertRepository repository,
    IAlertRuleRepository ruleRepository,
    ITenantAccessor tenantAccessor,
    IAlertDeliveryService deliveryService,
    ISensorContextEnricher contextEnricher,
    IExcursionResolutionHandler resolutionHandler,
    ConditionVersionLog conditionLog,
    IAlertRuleConditionValidator conditionValidator,
    TimeProvider timeProvider,
    ILogger<UnevaluableRuleNotice> logger)
    : IUnevaluableRuleNotice
{
    private const string BodyScope = "condition";

    /// <summary>The version of a rule's condition a notice is sent for.</summary>
    internal static string VersionOf(AlertRuleSnapshot rule) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{AlertConditionTypeNames.ToWireString(rule.ConditionType)}\n{rule.ConditionParams}")));

    public void Report(IReadOnlyList<AlertRuleSnapshot> rules)
    {
        var tenantId = tenantAccessor.TenantId;
        foreach (var rule in rules)
            Issues(rule, tenantId);
    }

    public async Task CheckAsync(IReadOnlyList<AlertRuleSnapshot> enabledRules, CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;
        if (tenantId == Guid.Empty) return;

        var standing = await repository.GetStandingUnevaluableNoticesAsync(null, ct);
        var enabled = enabledRules.Select(r => r.Id).ToHashSet();

        foreach (var rule in enabledRules)
        {
            if (Issues(rule, tenantId) is not { } issues)
                continue;

            var version = issues.Any(i => i.Scope == BodyScope) ? VersionOf(rule) : null;
            foreach (var stale in standing.Where(n => n.AlertRuleId == rule.Id && n.ConditionVersion != version))
                await TryResolveAsync(stale, ExcursionCloseReason.Manual, ct);

            if (version is null
                || standing.Any(n => n.AlertRuleId == rule.Id && n.ConditionVersion == version && n.Dispatched))
                continue;

            try
            {
                await NotifyAsync(rule, version, tenantId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "Sending the notice that alert rule {AlertRuleId} for tenant {TenantId} cannot be evaluated failed",
                    rule.Id, tenantId);
            }
        }

        foreach (var orphan in standing.Where(n => !enabled.Contains(n.AlertRuleId)))
            await TryResolveAsync(orphan, ExcursionCloseReason.RuleDisabled, ct);
    }

    public async Task ResolveAsync(Guid ruleId, ExcursionCloseReason reason, CancellationToken ct)
    {
        foreach (var notice in await repository.GetStandingUnevaluableNoticesAsync(ruleId, ct))
            await ResolveNoticeAsync(notice.ExcursionId, reason, ct);
    }

    /// <summary>Resolves one notice, logging a failure so it never stops the rest of the run.</summary>
    private async Task TryResolveAsync(StandingUnevaluableNotice notice, ExcursionCloseReason reason, CancellationToken ct)
    {
        try
        {
            await ResolveNoticeAsync(notice.ExcursionId, reason, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Resolving notice {ExcursionId} that alert rule {AlertRuleId} cannot be evaluated failed",
                notice.ExcursionId, notice.AlertRuleId);
        }
    }

    /// <summary>
    /// The problems with the rule's trees at this version of its condition, logged the first time
    /// this process sees the version; null when the check failed.
    /// </summary>
    private IReadOnlyList<RustValidationIssue>? Issues(AlertRuleSnapshot rule, Guid tenantId)
    {
        IReadOnlyList<RustValidationIssue>? issues;
        bool validated;
        try
        {
            issues = conditionLog.IssuesFor(rule.Id, rule.ConditionType, rule.ConditionParams,
                () => conditionValidator.Validate(
                    rule.ConditionType, rule.ConditionParams, rule.AutoResolveEnabled,
                    rule.AutoResolveParams, rule.ClientConfiguration),
                out validated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Checking the conditions of alert rule {AlertRuleId} for tenant {TenantId} failed",
                rule.Id, tenantId);
            return null;
        }

        if (validated && issues is { Count: > 0 })
        {
            logger.LogWarning(
                "Alert rule {AlertRuleId} for tenant {TenantId} has conditions that cannot be evaluated until it is edited: {Issues}",
                rule.Id,
                tenantId,
                string.Join("; ", issues.Select(i => $"{i.Scope} {i.Path}: {i.Reason}{(i.Field is null ? "" : $" ({i.Field})")}")));
        }
        return issues;
    }

    private async Task NotifyAsync(AlertRuleSnapshot rule, string version, Guid tenantId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (await repository.RecordUnevaluableNoticeAsync(rule, version, now, ct) is not { Dispatched: false } notice)
            return;

        var channels = (await ruleRepository.GetChannelsAsync([rule.Id], ct)).GetValueOrDefault(rule.Id) ?? [];
        var activeExcursionCount = await repository.CountActiveExcursionsAsync(ct);
        var tenant = await repository.GetTenantAlertContextAsync(tenantId, ct);
        var payload = AlertPayloads.Unevaluable(
            rule, tenantId, notice.ExcursionId, notice.InstanceId, tenant, activeExcursionCount, now);

        var dnd = await contextEnricher.EnrichAsync(
            new SensorContext { LatestValue = null, LatestTimestamp = null, TrendRate = null, LastReadingAt = null }, [], ct);
        await DndGatedDispatch.DispatchAsync(
            rule, dnd.ActiveDndScopes, notice.InstanceId, channels, payload, repository, deliveryService, logger, ct);

        logger.LogInformation(
            "Notice {InstanceId} sent that alert rule {AlertRuleId} cannot be evaluated until it is edited",
            notice.InstanceId, rule.Id);

        // A retirement that ran during the dispatch resolved the notice before a provider made what
        // the resolution clears (an in-app notification), so resolve it again.
        if (!(await repository.GetStandingUnevaluableNoticesAsync(rule.Id, ct)).Any(n => n.ExcursionId == notice.ExcursionId))
            await ResolveNoticeAsync(notice.ExcursionId, ExcursionCloseReason.RuleDisabled, ct);
    }

    private async Task ResolveNoticeAsync(Guid excursionId, ExcursionCloseReason reason, CancellationToken ct)
    {
        await repository.EndUnevaluableNoticeAsync(excursionId, timeProvider.GetUtcNow().UtcDateTime, ct);
        await resolutionHandler.HandleClosedAsync(
            new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, excursionId, reason),
            tenantAccessor.TenantId,
            ct);
    }
}
