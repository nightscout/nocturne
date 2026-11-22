using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using NoticeRule = Nocturne.API.Services.Alerts.RuleNoticeDispatch.NoticeRule;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Tells a rule's recipients, once per hold, that the rule has been waiting for usable data for
/// the engine's hold bound (docs/alerts/engine-semantics.md §6.4), and takes the notice back once
/// the hold is over. A sibling of <see cref="IUnevaluableRuleNotice"/> on the same storage,
/// dispatch and once-only record.
/// </summary>
/// <remarks>
/// <para>
/// The notice is derived from the rule's stored tracker state, not from the evaluation that
/// reached the bound: a state whose hold has been reported (its <c>UpdatedAt</c> at or past
/// <c>HeldSince</c> plus <see cref="RustAlertEngine.HoldBound"/>) owes a notice for that
/// <c>HeldSince</c>. So a process that dies between the evaluation's write and the send is made
/// good by the next sweep run. Its row (<see cref="IAlertRepository.RecordHeldNoticeAsync"/>) is
/// recorded owing its dispatch, which is settled once the dispatch returns, and found again by the
/// hold it is for, so a restart or a second instance does not send it again.
/// </para>
/// <para>
/// Each run also resolves every standing notice whose rule is no longer enabled or no longer
/// holds since the instant it was sent for: the hold ended at a known root, or a new hold began.
/// A rule disabled while held is never sent one, since only enabled rules are.
/// </para>
/// </remarks>
public interface IHeldRuleNotice
{
    /// <summary>
    /// Sends the notice for each rule in <paramref name="enabled"/> (every enabled rule of the
    /// tenant) whose hold was reported and has no notice dispatched, and resolves every other
    /// standing one. A data or provider failure for one rule is logged and never stops the others.
    /// A hold an older engine wrote over (<see cref="HeldRuleState.WrittenOver"/>) is not sent: the
    /// engine restarts it at the rule's next held evaluation.
    /// </summary>
    Task CheckAsync(IReadOnlyList<AlertRuleSnapshot> enabled, CancellationToken ct);

    /// <summary>
    /// The enabled rules whose hold has lasted the bound and is not yet reported: the sweep
    /// evaluates them, so a rule that reads nothing on the clock reaches the bound.
    /// </summary>
    Task<IReadOnlyList<AlertRuleSnapshot>> DueAsync(IReadOnlyList<AlertRuleSnapshot> enabled, DateTime now, CancellationToken ct);
}

internal sealed class HeldRuleNotice(
    IAlertRepository repository,
    IAlertRuleRepository ruleRepository,
    ITenantAccessor tenantAccessor,
    IAlertDeliveryService deliveryService,
    ISensorContextEnricher contextEnricher,
    IExcursionResolutionHandler resolutionHandler,
    TimeProvider timeProvider,
    ILogger<HeldRuleNotice> logger)
    : IHeldRuleNotice
{
    public async Task<IReadOnlyList<AlertRuleSnapshot>> DueAsync(
        IReadOnlyList<AlertRuleSnapshot> enabled, DateTime now, CancellationToken ct)
    {
        var bound = RustAlertEngine.HoldBound();
        var due = (await repository.GetHeldRuleStatesAsync(ct))
            .Where(h => h.HeldSince + bound <= now && h.UpdatedAt < h.HeldSince + bound)
            .Select(h => h.AlertRuleId)
            .ToHashSet();
        return enabled.Where(r => due.Contains(r.Id)).ToList();
    }

    public async Task CheckAsync(IReadOnlyList<AlertRuleSnapshot> enabled, CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;
        if (tenantId == Guid.Empty) return;

        var bound = RustAlertEngine.HoldBound();
        var held = (await repository.GetHeldRuleStatesAsync(ct)).ToDictionary(h => h.AlertRuleId);
        var standing = await repository.GetStandingHeldNoticesAsync(null, ct);
        var enabledById = enabled.ToDictionary(r => r.Id);

        foreach (var notice in standing.Where(n =>
                     !enabledById.ContainsKey(n.AlertRuleId)
                     || !held.TryGetValue(n.AlertRuleId, out var h)
                     || h.HeldSince != n.HeldSince))
        {
            await IsolatedAsync(notice.AlertRuleId, "resolving", () => ResolveNoticeAsync(notice.ExcursionId, ct));
        }

        foreach (var hold in held.Values.Where(h => h.UpdatedAt >= h.HeldSince + bound && !h.WrittenOver(bound)))
        {
            if (!enabledById.TryGetValue(hold.AlertRuleId, out var rule)
                || standing.Any(n => n.AlertRuleId == rule.Id && n.HeldSince == hold.HeldSince && n.Dispatched))
                continue;
            await IsolatedAsync(rule.Id, "sending", () => NotifyAsync(rule, hold, tenantId, bound, ct));
        }
    }

    /// <summary>
    /// Runs one rule's notice work, logging a data or provider failure so that it never stops the
    /// other rules' notices; the next run tries it again.
    /// </summary>
    private async Task IsolatedAsync(Guid ruleId, string what, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (DbException ex)
        {
            Failed(ex, ruleId, what);
        }
        catch (DbUpdateException ex)
        {
            Failed(ex, ruleId, what);
        }
        catch (HttpRequestException ex)
        {
            Failed(ex, ruleId, what);
        }
        catch (TimeoutException ex)
        {
            Failed(ex, ruleId, what);
        }
    }

    private void Failed(Exception ex, Guid ruleId, string what) =>
        logger.LogError(ex,
            "{What} the notice that alert rule {AlertRuleId} has waited for usable data failed; the next run tries again",
            what, ruleId);

    private async Task NotifyAsync(
        AlertRuleSnapshot rule, HeldRuleState hold, Guid tenantId, TimeSpan bound, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (await repository.RecordHeldNoticeAsync(rule.Id, hold.HeldSince, now, ct) is not { Dispatched: false } notice)
            return;

        var closedAlert = await repository.HadExcursionOpenAtAsync(rule.Id, hold.HeldSince + bound, ct);
        await RuleNoticeDispatch.DispatchAsync(
            NoticeRule.Of(rule),
            notice,
            (tenant, activeExcursionCount) => AlertPayloads.Held(
                rule.Name, rule.ConditionType, rule.Severity, closedAlert, HeldFacts.Parse(hold.HeldFacts),
                tenantId, notice.ExcursionId, notice.InstanceId, tenant, activeExcursionCount, now),
            tenantId, repository, ruleRepository, deliveryService, contextEnricher, logger, ct);

        logger.LogInformation(
            "Notice {InstanceId} sent that alert rule {AlertRuleId} has waited for usable data since {HeldSince}",
            notice.InstanceId, rule.Id, hold.HeldSince);

        // A retirement or the hold's end during the dispatch resolved the notice before a provider
        // made what the resolution clears (an in-app notification), so resolve it again.
        if (!(await repository.GetStandingHeldNoticesAsync(rule.Id, ct)).Any(n => n.ExcursionId == notice.ExcursionId))
            await ResolveNoticeAsync(notice.ExcursionId, ct);
    }

    private async Task ResolveNoticeAsync(Guid excursionId, CancellationToken ct)
    {
        await repository.EndNoticeAsync(excursionId, timeProvider.GetUtcNow().UtcDateTime, ct);
        await resolutionHandler.HandleClosedAsync(
            new ExcursionTransition(ExcursionTransitionType.ExcursionClosed, excursionId, ExcursionCloseReason.Manual),
            tenantAccessor.TenantId,
            ct);
    }
}
