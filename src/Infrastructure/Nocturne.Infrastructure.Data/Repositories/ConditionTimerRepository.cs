using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IConditionTimerStore"/> backed by the
/// <c>alert_condition_timers</c> (runs) and <c>alert_condition_not_true</c> tables. Tenant scoping is provided by the
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
    /// <remarks>
    /// A path with a row in both tables is a run: an image that knows only
    /// <c>alert_condition_timers</c> can start one without removing the other row.
    /// </remarks>
    public virtual async Task<ConditionTimers> GetAllForRuleAsync(Guid ruleId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var running = await db.AlertConditionTimers
            .AsNoTracking()
            .Where(t => t.AlertRuleId == ruleId)
            .ToDictionaryAsync(t => t.ConditionPath, t => t.FirstTrueAt, ct);
        var notTrue = await db.AlertConditionNotTrue
            .AsNoTracking()
            .Where(t => t.AlertRuleId == ruleId)
            .ToDictionaryAsync(t => t.ConditionPath, t => t.NotTrueAt, ct);
        foreach (var path in running.Keys)
            notTrue.Remove(path);

        return new ConditionTimers(running, notTrue);
    }

    /// <inheritdoc />
    public virtual async Task SetFirstTrueAsync(Guid ruleId, string path, DateTime at, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var run = await db.AlertConditionTimers
            .FirstOrDefaultAsync(t => t.AlertRuleId == ruleId && t.ConditionPath == path, ct);
        if (run == null)
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
            run.FirstTrueAt = at;
        }

        if (await db.AlertConditionNotTrue
                .FirstOrDefaultAsync(t => t.AlertRuleId == ruleId && t.ConditionPath == path, ct) is { } notTrue)
            db.AlertConditionNotTrue.Remove(notTrue);

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public virtual async Task SetNotTrueAsync(Guid ruleId, string path, DateTime at, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var notTrue = await db.AlertConditionNotTrue
            .FirstOrDefaultAsync(t => t.AlertRuleId == ruleId && t.ConditionPath == path, ct);
        if (notTrue == null)
        {
            db.AlertConditionNotTrue.Add(new AlertConditionNotTrueEntity
            {
                TenantId = db.TenantId,
                AlertRuleId = ruleId,
                ConditionPath = path,
                NotTrueAt = at,
            });
        }
        else
        {
            notTrue.NotTrueAt = at;
        }

        if (await db.AlertConditionTimers
                .FirstOrDefaultAsync(t => t.AlertRuleId == ruleId && t.ConditionPath == path, ct) is { } run)
            db.AlertConditionTimers.Remove(run);

        await db.SaveChangesAsync(ct);
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

        var runs = await db.AlertConditionTimers
            .Where(t => t.AlertRuleId == ruleId && !retained.Contains(t.ConditionPath))
            .ToListAsync(ct);
        var notTrue = await db.AlertConditionNotTrue
            .Where(t => t.AlertRuleId == ruleId && !retained.Contains(t.ConditionPath))
            .ToListAsync(ct);

        if (runs.Count == 0 && notTrue.Count == 0)
        {
            return;
        }

        db.AlertConditionTimers.RemoveRange(runs);
        db.AlertConditionNotTrue.RemoveRange(notTrue);
        await db.SaveChangesAsync(ct);
    }
}
