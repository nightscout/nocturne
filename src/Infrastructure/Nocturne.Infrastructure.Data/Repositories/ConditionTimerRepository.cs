using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IConditionTimerStore"/> backed by the
/// <c>alert_condition_timers</c> table. Tenant scoping is provided by the
/// <see cref="NocturneDbContext"/> global query filter on <see cref="ITenantScoped"/>.
/// Methods are virtual to allow mocking with CallBase in tests.
/// </summary>
public class ConditionTimerRepository : IConditionTimerStore
{
    private readonly NocturneDbContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConditionTimerRepository"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    public ConditionTimerRepository(NocturneDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public virtual async Task<DateTime?> GetFirstTrueAsync(Guid ruleId, string path, CancellationToken ct)
    {
        var row = await _context.AlertConditionTimers
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.AlertRuleId == ruleId && t.ConditionPath == path, ct);

        return row?.FirstTrueAt;
    }

    /// <inheritdoc />
    public virtual async Task SetFirstTrueAsync(Guid ruleId, string path, DateTime at, CancellationToken ct)
    {
        var existing = await _context.AlertConditionTimers
            .FirstOrDefaultAsync(t => t.AlertRuleId == ruleId && t.ConditionPath == path, ct);

        if (existing == null)
        {
            _context.AlertConditionTimers.Add(new AlertConditionTimerEntity
            {
                TenantId = _context.TenantId,
                AlertRuleId = ruleId,
                ConditionPath = path,
                FirstTrueAt = at,
            });
        }
        else
        {
            existing.FirstTrueAt = at;
        }

        await _context.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public virtual async Task ClearAsync(Guid ruleId, string path, CancellationToken ct)
    {
        var existing = await _context.AlertConditionTimers
            .FirstOrDefaultAsync(t => t.AlertRuleId == ruleId && t.ConditionPath == path, ct);

        if (existing != null)
        {
            _context.AlertConditionTimers.Remove(existing);
            await _context.SaveChangesAsync(ct);
        }
    }

    /// <inheritdoc />
    public virtual async Task ClearAllForRuleAsync(Guid ruleId, CancellationToken ct)
    {
        var rows = await _context.AlertConditionTimers
            .Where(t => t.AlertRuleId == ruleId)
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return;
        }

        _context.AlertConditionTimers.RemoveRange(rows);
        await _context.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public virtual async Task PruneToPathsAsync(
        Guid ruleId,
        IReadOnlyCollection<string> retainedPaths,
        CancellationToken ct)
    {
        var retained = retainedPaths as ICollection<string> ?? retainedPaths.ToHashSet();

        var rows = await _context.AlertConditionTimers
            .Where(t => t.AlertRuleId == ruleId && !retained.Contains(t.ConditionPath))
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return;
        }

        _context.AlertConditionTimers.RemoveRange(rows);
        await _context.SaveChangesAsync(ct);
    }
}
