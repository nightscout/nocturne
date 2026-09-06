using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// Repository for managing sleep sessions recorded by wearables or health platforms.
/// </summary>
public class SleepSessionRepository : ISleepSessionRepository
{
    private readonly ITenantDbContextFactory _contextFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SleepSessionRepository"/> class.
    /// </summary>
    /// <param name="contextFactory">The tenant database context factory.</param>
    public SleepSessionRepository(ITenantDbContextFactory contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SleepSession>> GetSessionsAsync(
        DateTime? from = null, DateTime? to = null, SleepSessionType? type = null, SleepSource? source = null,
        int limit = 100, int offset = 0, bool descending = true, bool includeStages = false,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(cancellationToken);
        var query = BuildFilteredQuery(ctx, from, to, type, source);
        if (includeStages)
            query = query.Include(e => e.Stages);
        query = descending
            ? query.OrderByDescending(e => e.StartTime)
            : query.OrderBy(e => e.StartTime);
        var entities = await query.Skip(offset).Take(limit).ToListAsync(cancellationToken);
        return entities.Select(e => SleepSessionMapper.ToDomainModel(e, includeChildren: includeStages));
    }

    /// <inheritdoc />
    public async Task<int> CountSessionsAsync(
        DateTime? from = null, DateTime? to = null, SleepSessionType? type = null, SleepSource? source = null,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(cancellationToken);
        var query = BuildFilteredQuery(ctx, from, to, type, source);
        return await query.CountAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SleepSession?> GetSessionByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(cancellationToken);
        var entity = await ctx.SleepSessions
            .Include(s => s.Stages)
            .Include(s => s.BiometricSamples)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        return entity is null ? null : SleepSessionMapper.ToDomainModel(entity, includeChildren: true);
    }

    /// <inheritdoc />
    public async Task<SleepSession> UpsertSessionAsync(SleepSession session, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(cancellationToken);
        var strategy = ctx.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(cancellationToken);
            var entity = SleepSessionMapper.ToEntity(session, ctx.TenantId);

            // Dedup by Source + OriginalId. When a prior sync of the same source
            // record exists, replace its contents in place: keep its primary key
            // so re-syncs don't churn the session id (and any reference to it).
            SleepSessionEntity? existing = null;
            if (!string.IsNullOrEmpty(entity.OriginalId))
            {
                existing = await ctx.SleepSessions
                    .Include(s => s.Stages)
                    .Include(s => s.BiometricSamples)
                    .FirstOrDefaultAsync(
                        s => s.Source == entity.Source && s.OriginalId == entity.OriginalId,
                        cancellationToken);
            }

            // Dedup by primary key. The mapper derives a deterministic entity Id
            // from an incoming session Id, so an upsert carrying an existing
            // session's Id (with a null or different OriginalId) must replace
            // that row rather than insert a duplicate key.
            existing ??= await ctx.SleepSessions
                .Include(s => s.Stages)
                .Include(s => s.BiometricSamples)
                .FirstOrDefaultAsync(s => s.Id == entity.Id, cancellationToken);

            if (existing is not null)
            {
                entity.Id = existing.Id;
                ctx.SleepBiometricSamples.RemoveRange(existing.BiometricSamples);
                ctx.SleepStages.RemoveRange(existing.Stages);
                ctx.SleepSessions.Remove(existing);
                await ctx.SaveChangesAsync(cancellationToken);
            }

            ctx.SleepSessions.Add(entity);
            await ctx.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return SleepSessionMapper.ToDomainModel(entity, includeChildren: true);
        });
    }

    /// <inheritdoc />
    public async Task<SleepSession?> UpdateSessionAsync(Guid id, SleepSession session, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(cancellationToken);
        var strategy = ctx.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var existing = await ctx.SleepSessions
                .Include(s => s.Stages)
                .Include(s => s.BiometricSamples)
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

            if (existing is null)
                return null;

            await using var tx = await ctx.Database.BeginTransactionAsync(cancellationToken);

            // Remove old entity and children, then insert updated version preserving the original ID
            ctx.SleepBiometricSamples.RemoveRange(existing.BiometricSamples);
            ctx.SleepStages.RemoveRange(existing.Stages);
            ctx.SleepSessions.Remove(existing);
            await ctx.SaveChangesAsync(cancellationToken);

            var entity = SleepSessionMapper.ToEntity(session, ctx.TenantId);
            entity.Id = id;
            ctx.SleepSessions.Add(entity);
            await ctx.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return SleepSessionMapper.ToDomainModel(entity, includeChildren: true);
        });
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSessionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateAsync(cancellationToken);
        var existing = await ctx.SleepSessions
            .Include(s => s.Stages)
            .Include(s => s.BiometricSamples)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (existing is null)
            return false;

        ctx.SleepBiometricSamples.RemoveRange(existing.BiometricSamples);
        ctx.SleepStages.RemoveRange(existing.Stages);
        ctx.SleepSessions.Remove(existing);
        await ctx.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static IQueryable<Entities.SleepSessionEntity> BuildFilteredQuery(
        NocturneDbContext ctx, DateTime? from, DateTime? to,
        SleepSessionType? type, SleepSource? source)
    {
        var query = ctx.SleepSessions.AsNoTracking();

        if (from.HasValue)
        {
            var fromValue = from.Value;
            query = query.Where(e => e.EndTime >= fromValue);
        }

        if (to.HasValue)
        {
            var toValue = to.Value;
            query = query.Where(e => e.StartTime <= toValue);
        }

        if (type.HasValue)
        {
            var typeValue = type.Value.ToString();
            query = query.Where(e => e.Type == typeValue);
        }

        if (source.HasValue)
        {
            var sourceValue = source.Value.ToString();
            query = query.Where(e => e.Source == sourceValue);
        }

        return query;
    }
}
