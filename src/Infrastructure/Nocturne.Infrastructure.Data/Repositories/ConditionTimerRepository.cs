using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IConditionTimerStore"/> backed by the
/// <c>alert_condition_timers</c> table. Tenant scoping is provided by the
/// <see cref="NocturneDbContext"/> global query filter on <see cref="ITenantScoped"/>.
/// Methods are virtual to allow mocking with CallBase in tests.
/// </summary>
public class ConditionTimerRepository : IConditionTimerStore
{
    private readonly INocturneDbContextFactory _contexts;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConditionTimerRepository"/> class.
    /// </summary>
    public ConditionTimerRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <inheritdoc />
    public virtual async Task<DateTime?> GetFirstTrueAsync(Guid ruleId, string path, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var row = await db.AlertConditionTimers
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.AlertRuleId == ruleId && t.ConditionPath == path, ct);

        return row?.FirstTrueAt;
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyDictionary<string, DateTime>> GetAllForRuleAsync(Guid ruleId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var rows = await db.AlertConditionTimers
            .AsNoTracking()
            .Where(t => t.AlertRuleId == ruleId)
            .Select(t => new { t.ConditionPath, t.FirstTrueAt })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.ConditionPath, r => r.FirstTrueAt);
    }

    /// <inheritdoc />
    public virtual async Task SetFirstTrueAsync(Guid ruleId, string path, DateTime at, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var existing = await db.AlertConditionTimers
            .FirstOrDefaultAsync(t => t.AlertRuleId == ruleId && t.ConditionPath == path, ct);

        if (existing == null)
        {
            db.AlertConditionTimers.Add(new AlertConditionTimerEntity
            {
                TenantId = db.TenantId,
                AlertRuleId = ruleId,
                ConditionPath = path,
                FirstTrueAt = at,
            });
        }
        else
        {
            existing.FirstTrueAt = at;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public virtual async Task ClearAsync(Guid ruleId, string path, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var existing = await db.AlertConditionTimers
            .FirstOrDefaultAsync(t => t.AlertRuleId == ruleId && t.ConditionPath == path, ct);

        if (existing != null)
        {
            db.AlertConditionTimers.Remove(existing);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <inheritdoc />
    public virtual async Task ClearAllForRuleAsync(Guid ruleId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var rows = await db.AlertConditionTimers
            .Where(t => t.AlertRuleId == ruleId)
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return;
        }

        db.AlertConditionTimers.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public virtual async Task PruneToPathsAsync(
        Guid ruleId,
        IReadOnlyCollection<string> retainedPaths,
        CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var retained = retainedPaths as ICollection<string> ?? retainedPaths.ToHashSet();

        var rows = await db.AlertConditionTimers
            .Where(t => t.AlertRuleId == ruleId && !retained.Contains(t.ConditionPath))
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return;
        }

        db.AlertConditionTimers.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
    }
}
