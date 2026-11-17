using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IConditionTimerStore"/> backed by the
/// <c>alert_condition_timers</c> table. Tenant scoping is provided by the
/// <see cref="NocturneDbContext"/> global query filter on <see cref="ITenantScoped"/>. Each call
/// runs on its own context, or on the caller's <see cref="DbUnitOfWork"/> when constructed over one.
/// Methods are virtual to allow mocking with CallBase in tests.
/// </summary>
public class ConditionTimerRepository : IConditionTimerStore
{
    private readonly INocturneDbContextFactory? _contexts;
    private readonly DbUnitOfWork? _unit;

    /// <summary>A repository whose calls each open their own context.</summary>
    public ConditionTimerRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <summary>A repository whose calls run on <paramref name="unit"/>, which the caller owns.</summary>
    public ConditionTimerRepository(DbUnitOfWork unit)
    {
        _unit = unit;
    }

    private ValueTask<DbContextLease> OpenAsync(CancellationToken ct) => DbContextLease.OpenAsync(_unit, _contexts, ct);

    /// <inheritdoc />
    public virtual async Task<IReadOnlyDictionary<string, DateTime>> GetAllForRuleAsync(Guid ruleId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
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
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
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
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var existing = await db.AlertConditionTimers
            .FirstOrDefaultAsync(t => t.AlertRuleId == ruleId && t.ConditionPath == path, ct);

        if (existing != null)
        {
            db.AlertConditionTimers.Remove(existing);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <inheritdoc />
    public virtual async Task PruneToPathsAsync(
        Guid ruleId,
        IReadOnlyCollection<string> retainedPaths,
        CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
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
