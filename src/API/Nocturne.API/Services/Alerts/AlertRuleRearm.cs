using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Drops a rule's re-arm hold (docs/alerts/engine-semantics.md §6.3) once a write changes what
/// it was taken against. That is the rule's condition, auto-resolve configuration or
/// enablement. Every writer of alert rules goes through here.
/// </summary>
/// <param name="gate">The in-process per-rule lease the tracker holds across read, decide and write.</param>
public sealed class AlertRuleRearm(AlertRuleEvaluationGate gate)
{
    /// <summary>
    /// Clears the hold of each of <paramref name="ruleIds"/> that has one, after the rule changes
    /// are saved. Each clear is its own transaction, under the lease and transition lock the
    /// tracker's writer takes.
    /// </summary>
    /// <remarks>
    /// Under the lease, no evaluation in this process can read the hold and then find it cleared
    /// when it writes. Another replica reads outside the transition lock. An evaluation there
    /// that read the hold just before this clears it drops its decision, as for any state
    /// changed under it. The rule's next evaluation decides again.
    /// </remarks>
    public async Task ClearAsync(NocturneDbContext db, IReadOnlyCollection<Guid> ruleIds, CancellationToken ct)
    {
        if (ruleIds.Count == 0)
            return;
        var held = await db.AlertTrackerState
            .AsNoTracking()
            .Where(s => ruleIds.Contains(s.AlertRuleId) && s.AwaitingRearm)
            .Select(s => s.AlertRuleId)
            .ToListAsync(ct);

        var repository = new AlertTrackerRepository(db);
        foreach (var ruleId in held)
        {
            using var lease = await gate.AcquireAsync(ruleId, ct);
            if (db.Database.IsRelational())
            {
                await repository.ExecuteInTransactionAsync(async token =>
                {
                    await repository.LockRuleAsync(ruleId, token);
                    return await ClearOneAsync(db, ruleId, token);
                }, ct: ct);
            }
            else
            {
                // A store without transactions is one no other process shares.
                await ClearOneAsync(db, ruleId, ct);
            }
        }
    }

    private static async Task<bool> ClearOneAsync(NocturneDbContext db, Guid ruleId, CancellationToken ct)
    {
        var state = await db.AlertTrackerState.FirstOrDefaultAsync(s => s.AlertRuleId == ruleId, ct);
        if (state is not { AwaitingRearm: true })
            return false;
        state.AwaitingRearm = false;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
