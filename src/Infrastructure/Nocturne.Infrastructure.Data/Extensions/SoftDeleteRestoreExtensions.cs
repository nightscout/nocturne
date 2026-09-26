using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Restore and trash-listing over <see cref="ISoftDeletable.DeletedAt"/>, for every tenant-scoped
/// soft-deletable row.
/// </summary>
/// <remarks>
/// Parameterless <c>IgnoreQueryFilters()</c> drops <see cref="NocturneDbContext.TenantFilterKey"/>
/// along with the soft-delete filter, so the tenant predicate is re-applied by hand — without it a
/// restore would reach across tenants. The key is read through <see cref="EF.Property{TProperty}"/>
/// because <c>PatientDeviceEntity</c> and <c>PatientInsulinEntity</c> declare it on no interface.
/// </remarks>
public static class SoftDeleteRestoreExtensions
{
    private const string IdProperty = "Id";
    private const string LegacyIdProperty = nameof(IV4Entity.LegacyId);
    private const string DataSourceProperty = nameof(ISyncDedupable.DataSource);
    private const string SyncIdentifierProperty = nameof(ISyncDedupable.SyncIdentifier);

    /// <summary>
    /// Clears <see cref="ISoftDeletable.DeletedAt"/> on this tenant's soft-deleted row with the given
    /// key and returns the tracked entity. <paramref name="recordType"/> names the domain type in the
    /// not-found and conflict messages.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No soft-deleted row with that key in this tenant.</exception>
    /// <exception cref="RecreationBlockedException">
    /// A live row holds a unique key the restored row would take, per
    /// <see cref="FindLiveKeyClashesAsync{TEntity}"/> or <see cref="SaveRestoreAsync"/>. The row
    /// stays deleted.
    /// </exception>
    public static async Task<TEntity> RestoreDeletedAsync<TEntity>(
        this NocturneDbContext ctx, Guid id, string recordType, CancellationToken ct = default)
        where TEntity : class, ITenantScoped, ISoftDeletable
    {
        var entity = await ctx.DeletedRows<TEntity>()
            .Where(e => EF.Property<Guid>(e, IdProperty) == id)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Soft-deleted {recordType} {id} not found");

        if ((await ctx.FindLiveKeyClashesAsync([entity], ct))[0] is { } held)
            throw RecreationBlockedException.ForRestore(recordType, held);

        entity.DeletedAt = null;
        await ctx.SaveRestoreAsync(recordType, ct);
        return entity;
    }

    /// <summary>
    /// Clears <see cref="ISoftDeletable.DeletedAt"/> on every soft-deleted row of this tenant whose key
    /// is in <paramref name="ids"/>, and returns those rows. Keys that are unknown or already live are
    /// silently skipped. A row found by <see cref="FindLiveKeyClashesAsync{TEntity}"/> stays deleted
    /// and its key is returned in <see cref="BulkRestoreResult{T}.Conflicts"/>.
    /// </summary>
    /// <exception cref="RecreationBlockedException">
    /// The save lost the race <see cref="SaveRestoreAsync"/> describes. The whole batch stays deleted.
    /// </exception>
    public static async Task<BulkRestoreResult<TEntity>> RestoreDeletedAsync<TEntity>(
        this NocturneDbContext ctx, IEnumerable<Guid> ids, string recordType, CancellationToken ct = default)
        where TEntity : class, ITenantScoped, ISoftDeletable
    {
        var idSet = ids.ToHashSet();
        // Newest deletion first, so of two deleted rows sharing a key the later one is restored.
        var entities = await ctx.DeletedRows<TEntity>()
            .Where(e => idSet.Contains(EF.Property<Guid>(e, IdProperty)))
            .OrderByDescending(e => e.DeletedAt)
            .ToListAsync(ct);

        var clashes = await ctx.FindLiveKeyClashesAsync(entities, ct);
        var restored = new List<TEntity>(entities.Count);
        var conflicts = new List<Guid>();
        for (var i = 0; i < entities.Count; i++)
        {
            if (clashes[i] is null)
            {
                entities[i].DeletedAt = null;
                restored.Add(entities[i]);
            }
            else
            {
                conflicts.Add(ctx.Entry(entities[i]).Property<Guid>(IdProperty).CurrentValue);
            }
        }

        await ctx.SaveRestoreAsync(recordType, ct);
        return new BulkRestoreResult<TEntity> { Restored = restored, Conflicts = conflicts };
    }

    /// <summary>
    /// Saves a restore, answering a unique violation as the refusal the clash check would have given.
    /// A live row written after <see cref="FindLiveKeyClashesAsync{TEntity}"/> read the table is
    /// caught only by the index, and which key it took is not known here.
    /// </summary>
    private static async Task SaveRestoreAsync(this NocturneDbContext ctx, string recordType, CancellationToken ct)
    {
        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw RecreationBlockedException.ForRestore(recordType, "one of its unique keys");
        }
    }

    /// <summary>
    /// For each of <paramref name="candidates"/>, the unique key restoring it would clash on, or null
    /// when it restores cleanly. The keys are those the partial unique indexes over
    /// <c>deleted_at IS NULL</c> enforce: the legacy id on
    /// <see cref="NocturneDbContext.V4LegacyIdRecordEntities"/> and the sync key on
    /// <see cref="NocturneDbContext.SyncDedupedEntities"/>. A key is held by a live row of this
    /// tenant, or by an earlier candidate that restores cleanly.
    /// </summary>
    private static async Task<string?[]> FindLiveKeyClashesAsync<TEntity>(
        this NocturneDbContext ctx, IReadOnlyList<TEntity> candidates, CancellationToken ct)
        where TEntity : class, ITenantScoped, ISoftDeletable
    {
        var clashes = new string?[candidates.Count];
        var byLegacyId = NocturneDbContext.V4LegacyIdRecordEntities.Contains(typeof(TEntity));
        var bySyncKey = NocturneDbContext.SyncDedupedEntities.Contains(typeof(TEntity));
        if (candidates.Count == 0 || (!byLegacyId && !bySyncKey))
            return clashes;

        var live = ctx.Set<TEntity>().IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt == null);

        var keys = candidates.Select(c =>
        {
            var legacyId = byLegacyId ? ctx.KeyValue(c, LegacyIdProperty) : null;
            (string DataSource, string SyncIdentifier)? syncKey =
                bySyncKey
                && ctx.KeyValue(c, DataSourceProperty) is { } dataSource
                && ctx.KeyValue(c, SyncIdentifierProperty) is { } syncIdentifier
                    ? (dataSource, syncIdentifier)
                    : null;
            return (LegacyId: legacyId, SyncKey: syncKey);
        }).ToList();

        var heldLegacyIds = new HashSet<string>(StringComparer.Ordinal);
        var wantedLegacyIds = keys.Select(k => k.LegacyId).OfType<string>().Distinct().ToList();
        if (wantedLegacyIds.Count > 0)
        {
            heldLegacyIds.UnionWith(await live
                .Select(e => EF.Property<string>(e, LegacyIdProperty))
                .Where(legacyId => wantedLegacyIds.Contains(legacyId))
                .ToListAsync(ct));
        }

        var heldSyncKeys = new HashSet<(string DataSource, string SyncIdentifier)>();
        var wantedSyncIdentifiers = keys.Select(k => k.SyncKey?.SyncIdentifier).OfType<string>().Distinct().ToList();
        if (wantedSyncIdentifiers.Count > 0)
        {
            var rows = await live
                .Where(e => EF.Property<string?>(e, DataSourceProperty) != null
                         && wantedSyncIdentifiers.Contains(EF.Property<string>(e, SyncIdentifierProperty)))
                .Select(e => new
                {
                    DataSource = EF.Property<string>(e, DataSourceProperty),
                    SyncIdentifier = EF.Property<string>(e, SyncIdentifierProperty),
                })
                .ToListAsync(ct);
            heldSyncKeys.UnionWith(rows.Select(r => (r.DataSource, r.SyncIdentifier)));
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var (legacyId, syncKey) = keys[i];

            if (legacyId != null && heldLegacyIds.Contains(legacyId))
            {
                clashes[i] = RecreationBlockedException.LegacyIdIdentity(legacyId);
            }
            else if (syncKey is { } held && heldSyncKeys.Contains(held))
            {
                clashes[i] = RecreationBlockedException.SyncKeyIdentity(held.DataSource, held.SyncIdentifier);
            }
            else
            {
                if (legacyId != null)
                    heldLegacyIds.Add(legacyId);
                if (syncKey is { } taken)
                    heldSyncKeys.Add(taken);
            }
        }

        return clashes;
    }

    /// <remarks>
    /// Read through the change tracker because <c>TempBasalEntity</c> carries the sync key without
    /// implementing <see cref="ISyncDedupable"/>.
    /// </remarks>
    private static string? KeyValue(this NocturneDbContext ctx, object entity, string property)
        => (string?)ctx.Entry(entity).Property(property).CurrentValue;

    /// <summary>Pages this tenant's soft-deleted rows, newest deletion first.</summary>
    public static Task<List<TEntity>> GetDeletedAsync<TEntity>(
        this NocturneDbContext ctx, int limit, int offset, CancellationToken ct = default)
        where TEntity : class, ITenantScoped, ISoftDeletable
        => ctx.DeletedRows<TEntity>()
            .OrderByDescending(e => e.DeletedAt)
            .Skip(offset).Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);

    /// <summary>Counts this tenant's soft-deleted rows.</summary>
    public static Task<int> CountDeletedAsync<TEntity>(
        this NocturneDbContext ctx, CancellationToken ct = default)
        where TEntity : class, ITenantScoped, ISoftDeletable
        => ctx.DeletedRows<TEntity>().CountAsync(ct);

    private static IQueryable<TEntity> DeletedRows<TEntity>(this NocturneDbContext ctx)
        where TEntity : class, ITenantScoped, ISoftDeletable
        => ctx.Set<TEntity>().IgnoreQueryFilters()
            .Where(e => e.TenantId == ctx.TenantId && e.DeletedAt != null);
}
