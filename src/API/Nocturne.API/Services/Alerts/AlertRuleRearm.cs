using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Drops a rule's re-arm hold (docs/alerts/engine-semantics.md §6.3) once a write changes what
/// it was taken against. That is the rule's condition, auto-resolve configuration or
/// enablement. Every writer of alert rules goes through here.
/// </summary>
/// <remarks>
/// Under the lease, no evaluation can read the hold and then find it cleared when it writes, and
/// a hold one sets just before is read and cleared here.
/// <para>
/// An evaluation that read the rule before the save, and takes the lease after this clear,
/// decided on the old conditions. The writer re-reads the rule under the transition lock and
/// drops that decision (<see cref="AlertRuleConditions"/>), so it sets no hold.
/// </para>
/// </remarks>
/// <param name="gate">The in-process per-rule lease the tracker holds across read, decide and write.</param>
public sealed class AlertRuleRearm(AlertRuleEvaluationGate gate)
{
    /// <summary>
    /// Saves the changes <paramref name="unit"/> tracks for rule <paramref name="ruleId"/> and
    /// clears its hold in one transaction on that unit.
    /// </summary>
    /// <inheritdoc cref="SaveAndClearAsync(DbUnitOfWork, IReadOnlyCollection{Guid}, CancellationToken)"/>
    public Task SaveAndClearAsync(DbUnitOfWork unit, Guid ruleId, CancellationToken ct) =>
        SaveAndClearAsync(unit, [ruleId], ct);

    /// <summary>
    /// Saves the changes <paramref name="unit"/> tracks, clears the hold of each of
    /// <paramref name="ruleIds"/> and prunes its sustained timers to the paths its saved trees still
    /// have (<see cref="ConditionTimerPaths"/>), in one transaction on that unit, under the lease
    /// and transition lock the tracker's writer takes for each rule, so that none lands without
    /// the others.
    /// </summary>
    /// <remarks>
    /// Leases and locks are taken in rule id order, so two writers over overlapping rules cannot
    /// each hold one the other waits for.
    /// </remarks>
    /// <param name="unit">The tenant unit the rules were loaded and edited on.</param>
    /// <param name="ruleIds">The rules whose hold the saved changes invalidate.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task SaveAndClearAsync(
        DbUnitOfWork unit, IReadOnlyCollection<Guid> ruleIds, CancellationToken ct) =>
        SaveAsync(unit, ruleIds, clearHold: true, ct);

    /// <summary>
    /// Saves the changes <paramref name="unit"/> tracks for rule <paramref name="ruleId"/> and prunes
    /// its sustained timers as <see cref="SaveAndClearAsync(DbUnitOfWork, IReadOnlyCollection{Guid}, CancellationToken)"/>
    /// does, keeping its hold. For an edit of the smart-snooze conditions alone, which the hold was
    /// not taken against.
    /// </summary>
    public Task SaveAndPruneAsync(DbUnitOfWork unit, Guid ruleId, CancellationToken ct) =>
        SaveAsync(unit, [ruleId], clearHold: false, ct);

    private async Task SaveAsync(
        DbUnitOfWork unit, IReadOnlyCollection<Guid> ruleIds, bool clearHold, CancellationToken ct)
    {
        var ordered = ruleIds.Distinct().Order().ToList();
        var leases = new List<IDisposable>(ordered.Count);
        try
        {
            foreach (var ruleId in ordered)
                leases.Add(await gate.AcquireAsync(ruleId, ct));

            await new AlertTrackerRepository(unit).ExecuteInTransactionAsync(async (tracker, token) =>
            {
                foreach (var ruleId in ordered)
                    await tracker.LockRuleAsync(ruleId, token);
                await unit.Context.SaveChangesAsync(token);
                foreach (var ruleId in ordered)
                {
                    if (clearHold)
                        await ClearHoldAsync(tracker, ruleId, token);
                    if (await tracker.GetRuleAsync(ruleId, token) is { } rule)
                        await tracker.Timers.PruneToPathsAsync(ruleId, ConditionTimerPaths.Of(rule), token);
                }
                return true;
            }, ct: ct);
        }
        finally
        {
            for (var i = leases.Count - 1; i >= 0; i--)
                leases[i].Dispose();
        }
    }

    private static async Task<bool> ClearHoldAsync(
        IAlertTrackerRepository tracker, Guid ruleId, CancellationToken ct)
    {
        var state = await tracker.GetTrackerStateAsync(ruleId, ct);
        if (state is not { AwaitingRearm: true })
            return false;
        state.AwaitingRearm = false;
        await tracker.UpsertTrackerStateAsync(state, ct);
        return true;
    }
}
