using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IActivityRecordRepository"/> over <see cref="NocturneDbContext"/>, one context of the
/// scope's reader per call.
/// </summary>
public sealed class ActivityRecordRepository(INocturneDbContextFactory contexts, TimeProvider time)
    : IActivityRecordRepository
{
    private static readonly List<string> ActivityCategoryNames =
        ActivityStateSpanMapper.ActivityCategories.Select(c => c.ToString()).ToList();

    /// <inheritdoc />
    /// <remarks>
    /// One round trip per table for each of the reads by id and by sync key
    /// (<see cref="ResolveAsync{TModel,TEntity}"/>), each record planned onto its row in memory
    /// (<see cref="Plan{TModel,TEntity}"/>), then one save for both tables.
    /// </remarks>
    public async Task<ActivitySeriesWrites> WriteHealthSeriesAsync(
        IReadOnlyList<(int Index, HeartRate Record)> heartRates,
        IReadOnlyList<(int Index, StepCount Record)> stepCounts,
        CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var heartRateRows = await ResolveAsync(db, db.HeartRates, heartRates, HeartRateMapper.ToEntity, ct);
        var stepCountRows = await ResolveAsync(db, db.StepCounts, stepCounts, StepCountMapper.ToEntity, ct);

        var heartRatePlan = Plan(db, db.HeartRates, heartRateRows, HeartRateMapper.UpdateEntity);
        var stepCountPlan = Plan(db, db.StepCounts, stepCountRows, StepCountMapper.UpdateEntity);

        if (heartRatePlan.Writes || stepCountPlan.Writes)
            await db.SaveChangesAsync(ct);

        return new ActivitySeriesWrites(
            heartRatePlan.Written(HeartRateMapper.ToDomainModel),
            stepCountPlan.Written(StepCountMapper.ToDomainModel));
    }

    /// <inheritdoc />
    public async Task<bool> IsDeletedByUserAsync(string id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var recordId = RecordIdKey.ParseIdToGuid(id);

        return await db.UserTombstones<StateSpanEntity>().AnyAsync(
                s => ActivityCategoryNames.Contains(s.Category) && (s.LegacyId == id || s.Id == recordId), ct)
            || await db.UserTombstones<SleepSessionEntity>().AnyAsync(
                s => s.OriginalId == id || s.Id == recordId, ct)
            || await db.UserTombstones<HeartRateEntity>().AnyAsync(
                h => h.OriginalId == id || h.Id == recordId, ct)
            || await db.UserTombstones<StepCountEntity>().AnyAsync(
                s => s.OriginalId == id || s.Id == recordId, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Soft-deletes, as <see cref="Health.HealthSeriesRepository{TModel,TEntity}.DeleteAsync"/> does: the
    /// tombstone a user's delete leaves is what keeps a connector resync from bringing the record back
    /// (<see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>).
    /// </remarks>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateAsync(ct);
        var deleted = 0;
        var now = time.GetUtcNow().UtcDateTime;
        var recordId = RecordIdKey.ParseIdToGuid(legacyId);

        var heartRate = await db.HeartRates.FirstOrDefaultAsync(h => h.OriginalId == legacyId || h.Id == recordId, ct);
        if (heartRate != null)
        {
            heartRate.DeletedAt = now;
            deleted++;
        }

        var stepCount = await db.StepCounts.FirstOrDefaultAsync(s => s.OriginalId == legacyId || s.Id == recordId, ct);
        if (stepCount != null)
        {
            stepCount.DeletedAt = now;
            deleted++;
        }

        if (deleted > 0)
            await db.SaveChangesAsync(ct);

        return deleted;
    }

    /// <summary>A table's mapped records, by batch index, with the stored rows their ids and sync keys name.</summary>
    private sealed record Resolved<TModel, TEntity>(
        List<(int Index, TModel Model, TEntity Entity)> Records,
        Dictionary<string, TEntity> ById,
        HashSet<string> UserDeletedIds,
        Dictionary<(string? DataSource, string SyncIdentifier), TEntity> ByKey,
        HashSet<(string? DataSource, string SyncIdentifier)> UserDeletedKeys)
        where TEntity : class;

    /// <summary>
    /// Maps a table's records and reads, in one query each, the rows their ids name
    /// (<see cref="ResolveByIdAsync{TEntity}"/>) and the rows the sync keys of those with no id
    /// name (<see cref="ResolveBySyncKeyAsync{TEntity}"/>). Only a record with no id carries a sync key.
    /// </summary>
    private static async Task<Resolved<TModel, TEntity>> ResolveAsync<TModel, TEntity>(
        NocturneDbContext db, DbSet<TEntity> set, IReadOnlyList<(int Index, TModel Record)> models,
        Func<TModel, TEntity> toEntity, CancellationToken ct)
        where TModel : ProcessableDocumentBase
        where TEntity : class, ITenantScoped, ISoftDeletable, IOriginalIdentified, ISyncDedupable
    {
        var records = models.Select(m => (m.Index, Model: m.Record, Entity: toEntity(m.Record))).ToList();
        var (byId, userDeletedIds) = await ResolveByIdAsync(
            db, set,
            [.. records.Where(r => r.Model.Id != null).Select(r => (Id: r.Model.Id!, Key: r.Entity.Id)).DistinctBy(r => r.Id, StringComparer.Ordinal)],
            ct);
        var (byKey, userDeletedKeys) = await ResolveBySyncKeyAsync(
            set,
            [.. records.Where(r => r.Model.Id == null).Select(r => r.Entity.SyncIdentifier).OfType<string>().Distinct(StringComparer.Ordinal)],
            ct);
        return new(records, byId, userDeletedIds, byKey, userDeletedKeys);
    }

    /// <summary>
    /// The rows a table's records write, per <see cref="IActivityRecordRepository.WriteHealthSeriesAsync"/>.
    /// </summary>
    private static TablePlan<TModel, TEntity> Plan<TModel, TEntity>(
        NocturneDbContext db, DbSet<TEntity> set, Resolved<TModel, TEntity> resolved,
        Action<TEntity, TModel> applyUpdate)
        where TModel : ProcessableDocumentBase
        where TEntity : class, ITenantScoped, ISoftDeletable, IOriginalIdentified, ISyncDedupable
    {
        var plan = new TablePlan<TModel, TEntity>();
        var last = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var p = 0; p < resolved.Records.Count; p++)
        {
            if (KeyOf(resolved.Records[p].Model, resolved.Records[p].Entity) is { } key)
                last[key] = p;
        }

        for (var p = 0; p < resolved.Records.Count; p++)
        {
            var (index, model, entity) = resolved.Records[p];
            TEntity? stored = null;
            if (model.Id is { } id)
            {
                if (resolved.UserDeletedIds.Contains(id))
                {
                    plan.DeletedByUser.Add(index);
                    continue;
                }
                resolved.ById.TryGetValue(id, out stored);
            }
            else if (entity.SyncIdentifier is { } syncIdentifier)
            {
                if (resolved.UserDeletedKeys.Contains((entity.DataSource, syncIdentifier)))
                {
                    plan.DeletedByUser.Add(index);
                    continue;
                }
                resolved.ByKey.TryGetValue((entity.DataSource, syncIdentifier), out stored);
            }

            if (KeyOf(model, entity) is { } recordKey && last[recordKey] != p)
                continue;

            if (stored is null)
            {
                plan.Inserted.Add((index, entity));
                continue;
            }

            applyUpdate(stored, model);
            stored.DeletedAt = null;
            (db.Entry(stored).State == EntityState.Unchanged ? plan.Unchanged : plan.Updated).Add((index, stored));
        }

        set.AddRange(plan.Inserted.Select(i => i.Entity));
        return plan;
    }

    private static string? KeyOf<TModel, TEntity>(TModel model, TEntity entity)
        where TModel : ProcessableDocumentBase
        where TEntity : ISyncDedupable, IIdentified
        => model.Id is not null ? $"id|{entity.Id}"
            : entity.SyncIdentifier is { } syncIdentifier ? $"sync|{entity.DataSource}|{syncIdentifier}"
            : null;

    /// <summary>What <see cref="Plan{TModel,TEntity}"/> leaves a table's save to write, by batch index.</summary>
    private sealed class TablePlan<TModel, TEntity>
    {
        public List<int> DeletedByUser { get; } = [];
        public List<(int Index, TEntity Entity)> Inserted { get; } = [];
        public List<(int Index, TEntity Entity)> Updated { get; } = [];
        public List<(int Index, TEntity Entity)> Unchanged { get; } = [];

        public bool Writes => Inserted.Count > 0 || Updated.Count > 0;

        /// <summary>The committed rows as their models.</summary>
        public ActivitySeriesWrite<TModel> Written(Func<TEntity, TModel> toDomain)
        {
            return new ActivitySeriesWrite<TModel>(DeletedByUser, Fold(Inserted), Fold(Updated), Fold(Unchanged));

            List<(int Index, TModel Record)> Fold(List<(int Index, TEntity Entity)> rows) =>
                [.. rows.Select(r => (r.Index, toDomain(r.Entity)))];
        }
    }

    /// <summary>
    /// The row each of <paramref name="recordIds"/> is written to, soft-deleted rows included, found
    /// by <c>OriginalId</c> or by the primary key the record derives from its id
    /// (<see cref="RecordIdKey.ParseIdToGuid"/>), which a non-ObjectId id has no <c>OriginalId</c>
    /// to match: the live row, or else a system-swept tombstone, which the write revives in place
    /// because a fresh insert would collide with it within the tenant. An id only a user tombstone
    /// holds is returned in <c>UserDeleted</c> and must not be written
    /// (<see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>).
    /// </summary>
    private static async Task<(Dictionary<string, TEntity> Rows, HashSet<string> UserDeleted)> ResolveByIdAsync<TEntity>(
        NocturneDbContext db,
        DbSet<TEntity> set, IReadOnlyCollection<(string Id, Guid Key)> recordIds, CancellationToken ct)
        where TEntity : class, ITenantScoped, ISoftDeletable, IOriginalIdentified
    {
        var rows = new Dictionary<string, TEntity>(StringComparer.Ordinal);
        var userDeleted = new HashSet<string>(StringComparer.Ordinal);
        if (recordIds.Count == 0)
            return (rows, userDeleted);

        var ids = recordIds.Select(r => r.Id).ToList();
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        var idsByKey = recordIds.ToLookup(r => r.Key, r => r.Id);
        var keys = recordIds.Select(r => r.Key).Distinct().ToList();
        var candidates = await set.IncludingDeleted()
            .Where(e => ((e.OriginalId != null && ids.Contains(e.OriginalId)) || keys.Contains(e.Id)))
            .ToListAsync(ct);

        var named = candidates
            .SelectMany(e => idsByKey[e.Id].Prepend(e.OriginalId)
                .OfType<string>()
                .Where(wanted.Contains)
                .Distinct(StringComparer.Ordinal)
                .Select(id => (Id: id, Row: e)));
        foreach (var group in named.GroupBy(n => n.Id, n => n.Row, StringComparer.Ordinal))
        {
            if (group.FirstOrDefault(e => e.DeletedAt == null) is { } live)
                rows[group.Key] = live;
            else if (group.Any(e => db.BlocksRecreation(e)))
                userDeleted.Add(group.Key);
            else
                rows[group.Key] = group.First();
        }

        return (rows, userDeleted);
    }

    /// <summary>
    /// The live row holding each <c>(DataSource, SyncIdentifier)</c> key among
    /// <paramref name="syncIdentifiers"/>, and the keys only a user tombstone holds, which must not be
    /// written (<see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>). A key only a
    /// system sweep holds is in neither: the record inserts beside the tombstone, since the primary
    /// key of a record with no id is fresh and the sync-key unique index counts live rows only.
    /// </summary>
    private static async Task<(Dictionary<(string? DataSource, string SyncIdentifier), TEntity> Rows,
        HashSet<(string? DataSource, string SyncIdentifier)> UserDeleted)> ResolveBySyncKeyAsync<TEntity>(
        DbSet<TEntity> set, IReadOnlyCollection<string> syncIdentifiers, CancellationToken ct)
        where TEntity : class, ITenantScoped, ISoftDeletable, ISyncDedupable
    {
        var rows = new Dictionary<(string? DataSource, string SyncIdentifier), TEntity>();
        var userDeleted = new HashSet<(string? DataSource, string SyncIdentifier)>();
        if (syncIdentifiers.Count == 0)
            return (rows, userDeleted);

        var ids = syncIdentifiers.ToList();
        var candidates = await set.IncludingDeleted()
            .Where(e => e.SyncIdentifier != null && ids.Contains(e.SyncIdentifier))
            .WhereBlocksRecreation()
            .ToListAsync(ct);

        foreach (var group in candidates.GroupBy(e => (e.DataSource, SyncIdentifier: e.SyncIdentifier!)))
        {
            var governing = group.GoverningRow()!;
            if (governing.DeletedAt == null)
                rows[group.Key] = governing;
            else
                userDeleted.Add(group.Key);
        }

        return (rows, userDeleted);
    }
}
