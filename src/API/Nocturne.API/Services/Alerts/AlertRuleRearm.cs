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
/// <param name="repository">The tracker state store, whose transition lock the tracker's writer takes.</param>
public sealed class AlertRuleRearm(AlertRuleEvaluationGate gate, IAlertTrackerRepository repository)
{
    /// <summary>
    /// Clears the hold of each of <paramref name="ruleIds"/> that has one, after the rule changes
    /// are saved. Each rule is read and cleared in its own transaction, under the lease and
    /// transition lock the tracker's writer takes.
    /// </summary>
    public async Task ClearAsync(IReadOnlyCollection<Guid> ruleIds, CancellationToken ct)
    {
        foreach (var ruleId in ruleIds)
        {
            using var lease = await gate.AcquireAsync(ruleId, ct);
            await repository.ExecuteInTransactionAsync(async (tx, token) =>
            {
                await tx.LockRuleAsync(ruleId, token);
                return await ClearHoldAsync(tx, ruleId, token);
            }, ct: ct);
        }
    }

    /// <summary>
    /// Saves the changes <paramref name="unit"/> tracks for rule <paramref name="ruleId"/> and
    /// clears its hold in one transaction on that unit.
    /// </summary>
    /// <inheritdoc cref="SaveAndClearAsync(DbUnitOfWork, IReadOnlyCollection{Guid}, CancellationToken)"/>
    public Task SaveAndClearAsync(DbUnitOfWork unit, Guid ruleId, CancellationToken ct) =>
        SaveAndClearAsync(unit, [ruleId], ct);

    /// <summary>
    /// Saves the changes <paramref name="unit"/> tracks and clears the hold of each of
    /// <paramref name="ruleIds"/> in one transaction on that unit, under the lease and
    /// transition lock the tracker's writer takes for each rule, so that neither lands without
    /// the other.
    /// </summary>
    /// <remarks>
    /// Leases and locks are taken in rule id order, so two writers over overlapping rules cannot
    /// each hold one the other waits for.
    /// </remarks>
    /// <param name="unit">The tenant unit the rules were loaded and edited on.</param>
    /// <param name="ruleIds">The rules whose hold the saved changes invalidate.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveAndClearAsync(
        DbUnitOfWork unit, IReadOnlyCollection<Guid> ruleIds, CancellationToken ct)
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
                    await ClearHoldAsync(tracker, ruleId, token);
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
