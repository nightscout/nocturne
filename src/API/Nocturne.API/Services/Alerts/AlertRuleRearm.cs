using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Drops a rule's re-arm hold (docs/alerts/engine-semantics.md §6.3) and its recorded next change
/// (§5.3) once a write changes what they were taken against, the fields of
/// <see cref="AlertRuleConditions"/>, and has each such rule the save leaves enabled evaluated
/// against the current context (§5.2) once the save commits. Every writer that creates a rule,
/// enables one or changes its conditions goes through here, except the dev-only sample data seeder.
/// </summary>
/// <remarks>
/// Under the lease, no evaluation can read the hold and then find it cleared when it writes, and
/// a hold one sets just before is read and cleared here.
/// <para>
/// An evaluation that read the rule before the save, and takes the lease after this clear,
/// decided on the old conditions. The writer re-reads the rule under the transition lock and
/// drops that decision (<see cref="AlertRuleConditions"/>), so it sets no hold.
/// </para>
/// <para>
/// The evaluation is announced to <see cref="AlertFactEvaluationScheduler"/> after the commit and
/// the leases are released, and runs off the request, taking the lease and transition lock as any
/// pass does. A save inside a transaction its caller opened cannot see that commit: it keeps its
/// rules until the caller calls <see cref="Committed"/>.
/// </para>
/// </remarks>
/// <param name="gate">The in-process per-rule lease the tracker holds across read, decide and write.</param>
/// <param name="scheduler">Evaluates the saved rules off the request.</param>
/// <param name="tenant">The tenant the saves are made for.</param>
public sealed class AlertRuleRearm(
    AlertRuleEvaluationGate gate, AlertFactEvaluationScheduler scheduler, ITenantAccessor tenant)
{
    private readonly HashSet<Guid> _uncommitted = [];

    /// <summary>
    /// Saves the changes <paramref name="unit"/> tracks for rule <paramref name="ruleId"/> and
    /// clears its hold in one transaction on that unit.
    /// </summary>
    /// <inheritdoc cref="SaveAndClearAsync(DbUnitOfWork, IReadOnlyCollection{Guid}, CancellationToken)"/>
    public Task SaveAndClearAsync(DbUnitOfWork unit, Guid ruleId, CancellationToken ct) =>
        SaveAndClearAsync(unit, [ruleId], ct);

    /// <summary>
    /// Saves the changes <paramref name="unit"/> tracks, clears the hold and recorded next change
    /// of each of <paramref name="ruleIds"/> and prunes its sustained timers to the paths its
    /// saved trees still have (<see cref="ConditionTimerPaths"/>), in one transaction on that unit, under the lease
    /// and transition lock the tracker's writer takes for each rule, so that none lands without
    /// the others.
    /// </summary>
    /// <remarks>
    /// Leases and locks are taken in rule id order, so two writers over overlapping rules cannot
    /// each hold one the other waits for.
    /// </remarks>
    /// <param name="unit">The tenant unit the rules were loaded and edited on.</param>
    /// <param name="ruleIds">The rules whose hold the saved changes invalidate, and the rules the unit creates.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task SaveAndClearAsync(
        DbUnitOfWork unit, IReadOnlyCollection<Guid> ruleIds, CancellationToken ct) =>
        SaveAsync(unit, ruleIds, clearHold: true, ct);

    /// <summary>
    /// Saves the changes <paramref name="unit"/> tracks for rule <paramref name="ruleId"/> and prunes
    /// its sustained timers as <see cref="SaveAndClearAsync(DbUnitOfWork, IReadOnlyCollection{Guid}, CancellationToken)"/>
    /// does, keeping its hold and evaluating nothing. For an edit of the smart-snooze conditions alone,
    /// which the hold was not taken against and the rule's evaluation does not read.
    /// </summary>
    public Task SaveAndPruneAsync(DbUnitOfWork unit, Guid ruleId, CancellationToken ct) =>
        SaveAsync(unit, [ruleId], clearHold: false, ct);

    private async Task SaveAsync(
        DbUnitOfWork unit, IReadOnlyCollection<Guid> ruleIds, bool clearHold, CancellationToken ct)
    {
        var commits = unit.Context.Database.CurrentTransaction is null;
        var ordered = ruleIds.Distinct().Order().ToList();
        var leases = new List<IDisposable>(ordered.Count);
        var enabled = new List<Guid>(ordered.Count);
        try
        {
            foreach (var ruleId in ordered)
                leases.Add(await gate.AcquireAsync(ruleId, ct));

            await new AlertTrackerRepository(unit).ExecuteInTransactionAsync(async (tracker, token) =>
            {
                enabled.Clear();
                foreach (var ruleId in ordered)
                    await tracker.LockRuleAsync(ruleId, token);
                await unit.Context.SaveChangesAsync(token);
                foreach (var ruleId in ordered)
                {
                    if (clearHold)
                        await ClearHoldAsync(tracker, ruleId, token);
                    if (await tracker.GetRuleAsync(ruleId, token) is not { } rule)
                        continue;
                    await tracker.Timers.PruneToPathsAsync(ruleId, ConditionTimerPaths.Of(rule), token);
                    if (clearHold && rule.IsEnabled)
                        enabled.Add(ruleId);
                }
                return true;
            }, ct: ct);
        }
        finally
        {
            for (var i = leases.Count - 1; i >= 0; i--)
                leases[i].Dispose();
        }

        if (commits)
            Evaluate(enabled);
        else
            _uncommitted.UnionWith(enabled);
    }

    /// <summary>
    /// Has the rules saved inside a caller's transaction evaluated, once the caller has committed it.
    /// Ids saved by an attempt the caller rolled back are evaluated too: a rule that attempt only
    /// edited is judged as stored, and one it created does not exist, so it is skipped.
    /// </summary>
    public void Committed()
    {
        Evaluate(_uncommitted);
        _uncommitted.Clear();
    }

    private void Evaluate(IReadOnlyCollection<Guid> ruleIds)
    {
        if (ruleIds.Count > 0 && tenant.Context is { } context)
            scheduler.Moved(context, MovedFacts.Saved(ruleIds));
    }

    private static async Task ClearHoldAsync(
        IAlertTrackerRepository tracker, Guid ruleId, CancellationToken ct)
    {
        var state = await tracker.GetTrackerStateAsync(ruleId, ct);
        if (state is not ({ AwaitingRearm: true } or { NextChangeKnown: true }))
            return;
        state.AwaitingRearm = false;
        state.NextChangeKnown = false;
        state.NextChangeAt = null;
        state.NextChangeGlucose = null;
        await tracker.UpsertTrackerStateAsync(state, ct);
    }
}
