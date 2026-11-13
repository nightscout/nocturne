using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.Health;

/// <summary>
/// <see cref="HealthSeriesRepository{TModel,TEntity}"/> for a series keyed on its observation timestamp.
/// </summary>
/// <typeparam name="TModel">The domain record type.</typeparam>
/// <typeparam name="TEntity">The entity backing <typeparamref name="TModel"/>.</typeparam>
public abstract class ObservedHealthSeriesRepository<TModel, TEntity>(INocturneDbContextFactory contexts, TimeProvider time)
    : HealthSeriesRepository<TModel, TEntity>(contexts, time), IObservedHealthSeriesRepository<TModel>
    where TModel : class
    where TEntity : class, IOriginalIdentified, ITenantScoped, ISoftDeletable, ISyncDedupable, IObservationTimestamped
{
    protected sealed override IOrderedQueryable<TEntity> NewestFirst(IQueryable<TEntity> query) =>
        query.OrderByDescending(e => e.Timestamp);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TModel>> GetNewestFirstAsync(
        int count, int skip, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        await using var db = await Contexts.CreateAsync(ct);
        var query = Set(db).AsNoTracking();
        if (from is { } fromValue)
            query = query.Where(e => e.Timestamp >= fromValue);
        if (to is { } toValue)
            query = query.Where(e => e.Timestamp <= toValue);

        var entities = await NewestFirst(query)
            .Skip(skip)
            .Take(count)
            .ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TModel>> GetByDateRangeAsync(
        DateTime from, DateTime to, int? count, int skip, CancellationToken ct = default)
    {
        await using var db = await Contexts.CreateAsync(ct);
        var query = Set(db)
            .AsNoTracking()
            .Where(e => e.Timestamp >= from && e.Timestamp < to)
            .OrderBy(e => e.Timestamp)
            .Skip(skip);

        if (count is { } take)
            query = query.Take(take);

        var entities = await query.ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestTimestampAsync(string source, CancellationToken ct = default)
    {
        await using var db = await Contexts.CreateAsync(ct);
        return await Set(db)
            .AsNoTracking()
            .Where(e => e.DataSource == source)
            .MaxAsync(e => (DateTime?)e.Timestamp, ct);
    }
}
