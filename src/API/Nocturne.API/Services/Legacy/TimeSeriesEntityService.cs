using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Legacy;

/// <summary>
/// <see cref="SimpleEntityService{TDomain,TEntity}"/> for entities keyed on an observation
/// timestamp, adding the reads that timestamp makes possible: newest-first ordering, a half-open
/// <c>[from, to)</c> range page, and the per-source watermark a connector resumes from.
/// </summary>
/// <typeparam name="TDomain">The domain model type.</typeparam>
/// <typeparam name="TEntity">The EF Core entity type stored in the database.</typeparam>
public abstract class TimeSeriesEntityService<TDomain, TEntity>(
    NocturneDbContext dbContext,
    IDocumentProcessingService documentProcessingService,
    ISignalRBroadcastService signalRBroadcastService,
    ILogger logger
) : SimpleEntityService<TDomain, TEntity>(dbContext, documentProcessingService, signalRBroadcastService, logger)
    where TDomain : class, IProcessableDocument
    where TEntity : class, IOriginalIdentified, IObservationTimestamped, ISyncDedupable
{
    protected sealed override IOrderedQueryable<TEntity> OrderByTimestamp(IQueryable<TEntity> query) =>
        query.OrderByDescending(e => e.Timestamp);

    /// <summary>
    /// Retrieves records observed in <c>[from, to)</c>, oldest first. A <see langword="null"/>
    /// <paramref name="count"/> returns every record in the range.
    /// </summary>
    protected async Task<IEnumerable<TDomain>> GetByDateRangeAsync(
        DateTime from,
        DateTime to,
        int? count = null,
        int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        var query = EntitySet
            .Where(e => e.Timestamp >= from && e.Timestamp < to)
            .OrderBy(e => e.Timestamp)
            .Skip(skip);

        if (count is { } take)
            query = query.Take(take);

        var entities = await query.ToListAsync(cancellationToken);

        return entities.Select(ToDomainModel);
    }

    /// <summary>
    /// Returns the latest timestamp written by <paramref name="source"/> (a connector's resume
    /// watermark), or <see langword="null"/> when that source has written none.
    /// </summary>
    public Task<DateTime?> GetLatestTimestampAsync(
        string source,
        CancellationToken cancellationToken = default
    ) =>
        EntitySet
            .AsNoTracking()
            .Where(e => e.DataSource == source)
            .MaxAsync(e => (DateTime?)e.Timestamp, cancellationToken);
}
