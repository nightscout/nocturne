using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories.Health;

/// <summary>
/// <see cref="IHealthSeriesRepository{T}"/> over one health series table. These rows are not V4
/// records: they carry no legacy id, audit columns or duplicate links, so they upsert on the sync key
/// here rather than through <see cref="V4.SyncUpsertRepositoryBase{TModel,TEntity}"/>, sharing its
/// lookup, <see cref="SoftDeleteDedupExtensions.GoverningRowsBySyncKeyAsync{TEntity}"/>. Each call
/// runs on its own context, or on the caller's <see cref="DbUnitOfWork"/> when constructed over one.
/// </summary>
/// <typeparam name="TModel">The domain record type.</typeparam>
/// <typeparam name="TEntity">The entity backing <typeparamref name="TModel"/>.</typeparam>
public abstract class HealthSeriesRepository<TModel, TEntity> : IHealthSeriesRepository<TModel>
    where TModel : class
    where TEntity : class, IOriginalIdentified, ITenantScoped, ISoftDeletable, ISyncDedupable
{
    private readonly INocturneDbContextFactory? _contexts;
    private readonly DbUnitOfWork? _unit;
    private readonly TimeProvider _time;

    protected HealthSeriesRepository(INocturneDbContextFactory contexts, TimeProvider time)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    protected HealthSeriesRepository(DbUnitOfWork unit, TimeProvider time)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    protected ValueTask<DbContextLease> OpenAsync(CancellationToken ct) => DbContextLease.OpenAsync(_unit, _contexts, ct);

    protected abstract DbSet<TEntity> Set(NocturneDbContext db);

    protected abstract TModel ToDomain(TEntity entity);

    protected abstract TEntity ToEntity(TModel model);

    protected abstract void Apply(TEntity entity, TModel model);

    protected abstract IOrderedQueryable<TEntity> NewestFirst(IQueryable<TEntity> query);

    protected abstract IQueryable<TEntity> ObservedIn(IQueryable<TEntity> query, DateTime from, DateTime to);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TModel>> GetNewestFirstAsync(int count, int skip, CancellationToken ct = default)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entities = await NewestFirst(Set(db).AsNoTracking())
            .Skip(skip)
            .Take(count)
            .ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<TModel?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = await FindByIdAsync(db, id, ct);
        return entity is null ? null : ToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<bool> AnyObservedInAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var lease = await OpenAsync(ct);
        return await ObservedIn(Set(lease.Context).AsNoTracking(), from, to).AnyAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> CountObservedInAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var lease = await OpenAsync(ct);
        return await ObservedIn(Set(lease.Context).AsNoTracking(), from, to).CountAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TModel>> GetObservedInNewestFirstAsync(
        DateTime from, DateTime to, int count, CancellationToken ct = default)
    {
        await using var lease = await OpenAsync(ct);
        var entities = await NewestFirst(ObservedIn(Set(lease.Context).AsNoTracking(), from, to))
            .Take(count)
            .ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TModel>> UpsertAsync(IReadOnlyCollection<TModel> records, CancellationToken ct = default)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;

        // Two rows of one batch on the same sync key would collide on the partial unique index.
        var entities = records
            .Select(ToEntity)
            .Select((entity, index) => (entity, index))
            .GroupBy(item => item.entity.SyncKey() is { } key ? $"sync|{key}" : $"idx|{item.index}")
            .Select(group => group.Last().entity)
            .ToList();

        var hidden = new HashSet<TEntity>(ReferenceEqualityComparer.Instance);
        var bySyncKey = await Set(db).IncludingDeleted().GoverningRowsBySyncKeyAsync(entities, ct);
        if (db.HistoryClamped)
        {
            var missed = entities.Where(e => e.SyncKey() is { } key && !bySyncKey.ContainsKey(key)).ToList();
            if (missed.Count > 0)
            {
                var held = await db.WriteWithHistoryClampLiftedAsync(
                    token => Set(db).IncludingDeleted().GoverningRowsBySyncKeyAsync(missed, token), ct);
                foreach (var (key, row) in held)
                {
                    if (bySyncKey.ContainsKey(key))
                        continue;
                    bySyncKey[key] = row;
                    hidden.Add(row);
                }
            }
        }

        var stored = new List<(TEntity Row, TEntity Sent)>(entities.Count);
        var unmatched = new List<TEntity>();
        foreach (var entity in entities)
        {
            if (entity.SyncKey() is { } key && bySyncKey.TryGetValue(key, out var existing))
            {
                if (existing.DeletedAt == null)
                {
                    Apply(existing, ToDomain(entity));
                    stored.Add((existing, entity));
                }

                continue;
            }

            unmatched.Add(entity);
        }

        // A writer that derives the primary key from its own record id (Glooko, a hashed legacy id)
        // sends the same Id again, and a soft-deleted row keeps holding that key. A user tombstone
        // blocks the write; any other row is revived in place, since an insert would collide with it.
        var unmatchedIds = unmatched.Select(e => e.Id).ToList();
        var byId = unmatchedIds.Count == 0
            ? new Dictionary<Guid, TEntity>()
            : await Set(db).IncludingDeleted()
                .Where(e => unmatchedIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, ct);
        var missedIds = unmatchedIds.Where(id => !byId.ContainsKey(id)).ToList();
        if (db.HistoryClamped && missedIds.Count > 0)
        {
            var held = await db.WriteWithHistoryClampLiftedAsync(
                token => Set(db).IncludingDeleted().Where(e => missedIds.Contains(e.Id)).ToListAsync(token), ct);
            foreach (var row in held)
            {
                byId[row.Id] = row;
                hidden.Add(row);
            }
        }

        var toInsert = new List<TEntity>();
        foreach (var entity in unmatched)
        {
            if (!byId.TryGetValue(entity.Id, out var existing))
            {
                toInsert.Add(entity);
                stored.Add((entity, entity));
                continue;
            }

            if (existing.DeletedAt != null && db.BlocksRecreation(existing))
                continue;

            Apply(existing, ToDomain(entity));
            existing.DeletedAt = null;
            stored.Add((existing, entity));
        }

        if (toInsert.Count > 0)
            await Set(db).AddRangeAsync(toInsert, ct);
        if (hidden.Count == 0)
            await db.SaveChangesAsync(ct);
        else
            await db.WriteWithHistoryClampLiftedAsync(async token => await db.SaveChangesAsync(token), ct);

        return stored.Select(s => hidden.Contains(s.Row) ? AnswerHiddenRow(ToDomain(s.Row), ToDomain(s.Sent)) : ToDomain(s.Row)).ToList();
    }

    /// <summary>
    /// What an upsert answers for a row a history-clamped writer cannot see, which the writer's record overwrote:
    /// <paramref name="stored"/> with every field the record left to the stored row taken from <paramref name="sent"/>.
    /// The row is found by the sync key or the id the unique indexes hold over the whole tenant, so an insert
    /// beside it would fail with 23505. A series whose record overwrites everything its answer carries needs no override.
    /// </summary>
    protected virtual TModel AnswerHiddenRow(TModel stored, TModel sent) => stored;

    /// <inheritdoc />
    public async Task<TModel?> UpdateAsync(string id, TModel record, CancellationToken ct = default)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = await FindByIdAsync(db, id, ct);
        if (entity is null)
            return null;

        Apply(entity, record);
        await db.SaveChangesAsync(ct);
        return ToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = await FindByIdAsync(db, id, ct);
        if (entity is null)
            return false;

        entity.DeletedAt = _time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> PurgeSourceAsync(string source, CancellationToken ct = default)
    {
        await using var lease = await OpenAsync(ct);
        return await Set(lease.Context).PurgeAsync(e => e.DataSource == source, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceRecordKey>> GetSyncKeysAsync(
        string source, DateTime from, DateTime to, int limit, CancellationToken ct = default)
    {
        await using var lease = await OpenAsync(ct);
        return await ObservedIn(Set(lease.Context).AsNoTracking(), from, to)
            .Where(e => e.DataSource == source && e.DeletedAt == null && e.SyncIdentifier != null)
            .Select(e => new SourceRecordKey(e.Id, e.SyncIdentifier!))
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> RetireAsync(
        string source, IReadOnlyCollection<Guid> ids, DateTime deletedAt, CancellationToken ct = default)
    {
        await using var lease = await OpenAsync(ct);
        return await Set(lease.Context)
            .Where(e => e.DataSource == source && ids.Contains(e.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.DeletedAt, deletedAt)
                .SetProperty(e => EF.Property<bool>(e, NocturneDbContext.DeletedByUserProperty), false), ct);
    }

    private Task<TEntity?> FindByIdAsync(NocturneDbContext db, string id, CancellationToken ct) =>
        Set(db).FirstOrDefaultAsync(RecordIdKey.Matching<TEntity>(id), ct);
}
