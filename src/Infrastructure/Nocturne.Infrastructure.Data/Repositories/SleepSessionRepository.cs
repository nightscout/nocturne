using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// Repository for managing sleep sessions recorded by wearables or health platforms. Each call runs on
/// its own context, or on the caller's <see cref="DbUnitOfWork"/> when constructed over one.
/// </summary>
public class SleepSessionRepository : ISleepSessionRepository
{
    private readonly INocturneDbContextFactory? _contextFactory;
    private readonly DbUnitOfWork? _unit;
    private readonly TimeProvider _time;

    public SleepSessionRepository(INocturneDbContextFactory contextFactory, TimeProvider? time = null)
    {
        _contextFactory = contextFactory;
        _time = time ?? TimeProvider.System;
    }

    internal SleepSessionRepository(DbUnitOfWork unit, TimeProvider? time = null)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
        _time = time ?? TimeProvider.System;
    }

    private ValueTask<DbContextLease> OpenAsync(CancellationToken ct) =>
        DbContextLease.OpenAsync(_unit, _contextFactory, ct);

    /// <inheritdoc />
    public async Task<IEnumerable<SleepSession>> GetSessionsAsync(
        DateTime? from = null, DateTime? to = null, SleepSessionType? type = null, SleepSource? source = null,
        int limit = 100, int offset = 0, bool descending = true, bool includeStages = false,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await OpenAsync(cancellationToken);
        var ctx = lease.Context;
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
        await using var lease = await OpenAsync(cancellationToken);
        var ctx = lease.Context;
        var query = BuildFilteredQuery(ctx, from, to, type, source);
        return await query.CountAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<SleepSession?> GetSessionByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetSessionAsync(s => s.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<SleepSession?> GetSessionBySourceRecordAsync(
        SleepSource source, string originalId, CancellationToken cancellationToken = default)
    {
        var storedSource = source.ToString();
        return GetSessionAsync(s => s.Source == storedSource && s.OriginalId == originalId, cancellationToken);
    }

    /// <remarks>
    /// Stages and samples are sibling collections, so a single query joins them into
    /// stages x samples rows (80 x 480 = 38,400 for one overnight); the load is split.
    /// </remarks>
    private async Task<SleepSession?> GetSessionAsync(
        Expression<Func<SleepSessionEntity, bool>> predicate,
        CancellationToken cancellationToken)
    {
        await using var lease = await OpenAsync(cancellationToken);
        var ctx = lease.Context;
        var entity = await ctx.SleepSessions
            .Include(s => s.Stages)
            .Include(s => s.BiometricSamples)
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(predicate, cancellationToken);
        return entity is null ? null : SleepSessionMapper.ToDomainModel(entity, includeChildren: true);
    }

    /// <inheritdoc />
    public async Task<SleepSession> UpsertSessionAsync(SleepSession session, CancellationToken cancellationToken = default)
    {
        await using var lease = await OpenAsync(cancellationToken);
        var ctx = lease.Context;
        return await ctx.ExecuteInTransactionAsync(async token =>
        {
            var entity = SleepSessionMapper.ToEntity(session, ctx.TenantId);

            // Dedup by Source + OriginalId first: a re-sync of the same source record replaces
            // that row and keeps its primary key, so the session id (and any reference to it)
            // does not churn. Otherwise dedup by the primary key, which the mapper derives from
            // an incoming session Id, so an upsert carrying an existing session's Id (with a null
            // or different OriginalId) replaces that row rather than inserting a duplicate key.
            // Both lookups read soft-deleted rows: a user tombstone forbids re-creating the
            // session, and a system-swept one is replaced like a live row. The unique
            // (tenant, source, original_id) index counts soft-deleted rows, so inserting beside
            // a tombstone would violate it.
            if (!string.IsNullOrEmpty(entity.OriginalId))
            {
                await LockSourceRecordAsync(ctx, entity, token);
                var bySourceRecord = await WithSoftDeleted(ctx)
                    .AsNoTracking()
                    .Where(s => s.Source == entity.Source && s.OriginalId == entity.OriginalId)
                    .Select(s => (Guid?)s.Id)
                    .FirstOrDefaultAsync(token);
                entity.Id = bySourceRecord ?? entity.Id;
            }

            await LockIdAsync(ctx, entity.Id, token);
            var existing = await WithSoftDeleted(ctx).FirstOrDefaultAsync(s => s.Id == entity.Id, token);

            if (existing is { DeletedAt: not null } && ctx.BlocksRecreation(existing))
            {
                throw new RecreationBlockedException(
                    "sleep session", $"original id '{existing.OriginalId}' from '{existing.Source}', which the user deleted");
            }

            if (existing is not null)
            {
                entity.CreatedAt = existing.CreatedAt;
                ctx.SleepSessions.Remove(existing);
                await ctx.SaveChangesAsync(token);
            }

            ctx.SleepSessions.Add(entity);
            await ctx.SaveChangesAsync(token);
            return SleepSessionMapper.ToDomainModel(entity, includeChildren: true);
        }, ct: cancellationToken);
    }

    /// <summary>
    /// First key of the two-key advisory lock form for the source-record key. Each key kind has its
    /// own class, so a source-record hash colliding with an id hash is not the same lock.
    /// </summary>
    private const int SourceRecordLockClass = 0x534C_5352;

    /// <summary>First key of the two-key advisory lock form for the primary-key key.</summary>
    private const int IdLockClass = 0x534C_4944;

    /// <summary>
    /// The source-record half of the sleep-session key lock, taken first. It serialises writers of
    /// one key, so a concurrent duplicate waits for the first to commit and then replaces its row
    /// rather than failing a unique index or deleting a row already gone. Two keys of one kind
    /// whose hashes collide only wait for each other. A writer takes this before
    /// <see cref="LockIdAsync"/> and never the reverse, so two writers cannot deadlock.
    /// </summary>
    private static Task LockSourceRecordAsync(NocturneDbContext ctx, SleepSessionEntity entity, CancellationToken ct) =>
        ctx.LockForTransactionAsync(SourceRecordLockClass, $"{ctx.TenantId}|{entity.Source}|{entity.OriginalId}", ct);

    /// <summary>The primary-key half of the sleep-session key lock, taken last.</summary>
    private static Task LockIdAsync(NocturneDbContext ctx, Guid id, CancellationToken ct) =>
        ctx.LockForTransactionAsync(IdLockClass, $"{ctx.TenantId}|{id}", ct);

    /// <inheritdoc />
    public async Task<SleepSession?> UpdateSessionAsync(Guid id, SleepSession session, CancellationToken cancellationToken = default)
    {
        await using var lease = await OpenAsync(cancellationToken);
        var ctx = lease.Context;
        return await ctx.ExecuteInTransactionAsync<SleepSession?>(async token =>
        {
            var entity = SleepSessionMapper.ToEntity(session, ctx.TenantId);
            entity.Id = id;
            if (!string.IsNullOrEmpty(entity.OriginalId))
                await LockSourceRecordAsync(ctx, entity, token);
            await LockIdAsync(ctx, id, token);
            var existing = await ctx.SleepSessions.FirstOrDefaultAsync(s => s.Id == id, token);

            if (existing is null)
                return null;

            // The unique (tenant, source, original_id) index counts soft-deleted rows, so a move onto
            // a key another row holds must settle that row first. A live row or a user tombstone keeps
            // its key (see SoftDeleteDedupExtensions.WhereBlocksRecreation); a system sweep is replaced.
            if (!string.IsNullOrEmpty(entity.OriginalId))
            {
                var holder = await WithSoftDeleted(ctx)
                    .FirstOrDefaultAsync(
                        s => s.Id != id && s.Source == entity.Source && s.OriginalId == entity.OriginalId, token);
                if (holder is not null)
                {
                    if (ctx.BlocksRecreation(holder))
                    {
                        throw new RecreationBlockedException(
                            "sleep session",
                            $"original id '{holder.OriginalId}' from '{holder.Source}'"
                            + (holder.DeletedAt is null ? string.Empty : ", which the user deleted"));
                    }

                    ctx.SleepSessions.Remove(holder);
                }
            }

            entity.CreatedAt = existing.CreatedAt;
            ctx.SleepSessions.Remove(existing);
            await ctx.SaveChangesAsync(token);

            ctx.SleepSessions.Add(entity);
            await ctx.SaveChangesAsync(token);
            return SleepSessionMapper.ToDomainModel(entity, includeChildren: true);
        }, ct: cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Soft-deletes, keeping the stages and samples until the retention purge removes the session.
    /// A user's delete leaves a tombstone that keeps a resync from bringing the session back.
    /// </remarks>
    public async Task<bool> DeleteSessionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var lease = await OpenAsync(cancellationToken);
        var ctx = lease.Context;
        return await ctx.ExecuteInTransactionAsync(async token =>
        {
            await LockIdAsync(ctx, id, token);
            var existing = await ctx.SleepSessions.FirstOrDefaultAsync(s => s.Id == id, token);

            if (existing is null)
                return false;

            existing.DeletedAt = _time.GetUtcNow().UtcDateTime;
            await ctx.SaveChangesAsync(token);
            return true;
        }, ct: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> PurgeSourceAppAsync(
        SleepSource source, string sourceApp, CancellationToken cancellationToken = default)
    {
        await using var lease = await OpenAsync(cancellationToken);
        var storedSource = source.ToString();
        return await lease.Context.SleepSessions.PurgeAsync(
            s => s.Source == storedSource && s.SourceApp == sourceApp, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceRecordKey>> GetSourceAppKeysAsync(
        SleepSource source, string sourceApp, DateTime from, DateTime to, int limit,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await OpenAsync(cancellationToken);
        var storedSource = source.ToString();
        return await lease.Context.SleepSessions.AsNoTracking()
            .Where(s => s.Source == storedSource && s.SourceApp == sourceApp
                        && s.EndTime >= from && s.EndTime < to && s.OriginalId != null)
            .Select(s => new SourceRecordKey(s.Id, s.OriginalId!))
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> RetireAsync(
        SleepSource source, string sourceApp, IReadOnlyCollection<Guid> ids, DateTime deletedAt,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await OpenAsync(cancellationToken);
        var storedSource = source.ToString();
        return await lease.Context.SleepSessions
            .Where(s => s.Source == storedSource && s.SourceApp == sourceApp
                        && s.DeletedAt == null && ids.Contains(s.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.DeletedAt, deletedAt)
                .SetProperty(s => EF.Property<bool>(s, NocturneDbContext.DeletedByUserProperty), false),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SleepSessionStats?> DescribeSourceAppAsync(
        SleepSource source, string sourceApp, DateTime? since, DateTime last24Hours,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await OpenAsync(cancellationToken);
        var sessions = FromSourceApp(lease.Context, source, sourceApp).AsNoTracking();
        if (since is { } from)
            sessions = sessions.Where(s => s.StartTime >= from);
        return await sessions
            .GroupBy(_ => 1)
            .Select(g => new SleepSessionStats(
                g.LongCount(),
                g.Count(s => s.StartTime >= last24Hours),
                g.Max(s => s.StartTime),
                g.Min(s => s.StartTime)))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> DeleteSourceAppAsync(
        SleepSource source, string sourceApp, IAuditContext auditContext, string scope,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await OpenAsync(cancellationToken);
        var ctx = lease.Context;
        return await ctx.AuditedSoftDeleteAsync(
            FromSourceApp(ctx, source, sourceApp), auditContext, scope, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> AllowSourceAppReimportAsync(
        SleepSource source, string sourceApp, IAuditContext auditContext, string scope,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await OpenAsync(cancellationToken);
        var ctx = lease.Context;
        return await ctx.AuditedAllowReimportAsync(
            FromSourceApp(ctx, source, sourceApp), auditContext, scope, cancellationToken);
    }

    private static IQueryable<SleepSessionEntity> FromSourceApp(
        NocturneDbContext ctx, SleepSource source, string sourceApp)
    {
        var storedSource = source.ToString();
        return ctx.SleepSessions.Where(s => s.Source == storedSource && s.SourceApp == sourceApp);
    }

    /// <summary>
    /// This tenant's sessions, soft-deleted ones included, without their stages and samples:
    /// a writer that removes a session leaves those to the <c>ON DELETE CASCADE</c> foreign keys
    /// rather than loading and tracking every child only to delete it.
    /// </summary>
    private static IQueryable<SleepSessionEntity> WithSoftDeleted(NocturneDbContext ctx) =>
        ctx.SleepSessions.IncludingDeleted();

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
