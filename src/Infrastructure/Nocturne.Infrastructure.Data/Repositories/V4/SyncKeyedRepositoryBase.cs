using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.V4;

/// <summary>
/// <see cref="V4RepositoryBase{TModel,TEntity}"/> for the record types the upstream source names by an
/// <see cref="ISyncDedupable"/> key, adding the lookup and the delete issued against that key rather
/// than a Nocturne id — the surface a connector needs to reconcile its own records.
/// </summary>
/// <typeparam name="TModel">The V4 domain record type.</typeparam>
/// <typeparam name="TEntity">The EF entity type backing <typeparamref name="TModel"/>.</typeparam>
public abstract class SyncKeyedRepositoryBase<TModel, TEntity> : V4RepositoryBase<TModel, TEntity>
    where TModel : class, IV4Record
    where TEntity : class, IV4TimeSeriesEntity, IAuditable, ISyncDedupable
{
    /// <inheritdoc />
    protected SyncKeyedRepositoryBase(
        ITenantDbContextFactory contextFactory,
        IAuditContext auditContext,
        IV4RecordBroadcaster<TModel>? broadcaster = null,
        IDataEventSink<Entry>? entrySink = null)
        : base(contextFactory, auditContext, broadcaster, entrySink)
    {
    }

    /// <summary>
    /// Soft-deletes every live record matching the given (data source, sync identifier) pair. The
    /// global query filter scopes the lookup to the current tenant and skips rows already
    /// soft-deleted, so a repeat call for the same key returns 0.
    /// </summary>
    /// <param name="dataSource">The external data source name.</param>
    /// <param name="syncIdentifier">The external sync identifier.</param>
    /// <param name="origin">Whether the write is live or a backfill import.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of records soft-deleted.</returns>
    public async Task<int> DeleteBySyncIdentifierAsync(
        string dataSource, string syncIdentifier, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        return await AuditedSoftDeleteAndBroadcastAsync(
            ctx,
            ctx.Set<TEntity>().Where(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier),
            $"sync_identifier={dataSource}/{syncIdentifier}", origin, ct);
    }

    /// <summary>
    /// Finds a single record by data source and sync identifier. The global query filter
    /// automatically scopes the lookup to the current tenant and excludes soft-deleted rows.
    /// </summary>
    /// <param name="dataSource">The external data source name.</param>
    /// <param name="syncIdentifier">The external sync identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The matching record, or <see langword="null"/> when none matches.</returns>
    public async Task<TModel?> FindBySyncIdentifierAsync(
        string dataSource, string syncIdentifier, CancellationToken ct = default)
    {
        await using var ctx = await ContextFactory.CreateAsync(ct);
        var entity = await ctx.Set<TEntity>()
            .FirstOrDefaultAsync(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier, ct);
        return entity is null ? null : ToDomain(entity);
    }
}
