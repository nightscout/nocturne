using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>A restore made with <see cref="SoftDeleteRestoreExtensions.RestoreDeletedGroupAsync{TEntity}(NocturneDbContext, IEnumerable{Guid}, string, CancellationToken)"/>.</summary>
/// <param name="Restored">The requested rows restored.</param>
/// <param name="Conflicts">The requested ids left deleted for a key clash.</param>
/// <param name="Copies">The other copies in their duplicate groups restored with them.</param>
/// <param name="Promoted">The live rows made their group's primary because its primary stayed deleted.</param>
public sealed record GroupRestore<TEntity>(
    IReadOnlyList<TEntity> Restored,
    IReadOnlyList<Guid> Conflicts,
    IReadOnlyList<TEntity> Copies,
    IReadOnlyList<Guid> Promoted);

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
        => (await ctx.RestoreDeletedGroupAsync<TEntity>(id, recordType, ct)).Restored[0];

    /// <summary>
    /// <see cref="RestoreDeletedAsync{TEntity}(NocturneDbContext, Guid, string, CancellationToken)"/>,
    /// together with the copies in the row's duplicate group that were deleted with it
    /// (<see cref="RestoreInGroupsAsync{TEntity}"/>).
    /// </summary>
    /// <exception cref="KeyNotFoundException">No soft-deleted row with that key in this tenant.</exception>
    /// <exception cref="RecreationBlockedException">
    /// A live row holds a unique key the restored row would take. Nothing is restored.
    /// </exception>
    public static Task<GroupRestore<TEntity>> RestoreDeletedGroupAsync<TEntity>(
        this NocturneDbContext ctx, Guid id, string recordType, CancellationToken ct = default)
        where TEntity : class, ITenantScoped, ISoftDeletable
        => ctx.RestoreInGroupsAsync<TEntity>(async token =>
        {
            var entity = await ctx.DeletedRows<TEntity>()
                .Where(e => EF.Property<Guid>(e, IdProperty) == id)
                .FirstOrDefaultAsync(token)
                ?? throw new KeyNotFoundException($"Soft-deleted {recordType} {id} not found");

            if ((await ctx.FindLiveKeyClashesAsync([entity], token))[0] is { } held)
                throw RecreationBlockedException.ForRestore(recordType, held);

            return ([entity], []);
        }, recordType, ct);

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
        var restore = await ctx.RestoreDeletedGroupAsync<TEntity>(ids, recordType, ct);
        return new BulkRestoreResult<TEntity> { Restored = restore.Restored, Conflicts = restore.Conflicts };
    }

    /// <summary>
    /// <see cref="RestoreDeletedAsync{TEntity}(NocturneDbContext, IEnumerable{Guid}, string, CancellationToken)"/>,
    /// together with the copies in each restored row's duplicate group that were deleted with it
    /// (<see cref="RestoreInGroupsAsync{TEntity}"/>).
    /// </summary>
    /// <exception cref="RecreationBlockedException">
    /// The save lost the race <see cref="SaveRestoreAsync"/> describes. The whole batch stays deleted.
    /// </exception>
    public static Task<GroupRestore<TEntity>> RestoreDeletedGroupAsync<TEntity>(
        this NocturneDbContext ctx, IEnumerable<Guid> ids, string recordType, CancellationToken ct = default)
        where TEntity : class, ITenantScoped, ISoftDeletable
    {
        var idSet = ids.ToHashSet();
        return ctx.RestoreInGroupsAsync<TEntity>(async token =>
        {
            // Newest deletion first, so of two deleted rows sharing a key the later one is restored.
            var entities = await ctx.DeletedRows<TEntity>()
                .Where(e => idSet.Contains(EF.Property<Guid>(e, IdProperty)))
                .OrderByDescending(e => e.DeletedAt)
                .ToListAsync(token);

            var clashes = await ctx.FindLiveKeyClashesAsync(entities, token);
            var restoring = new List<TEntity>(entities.Count);
            var conflicts = new List<Guid>();
            for (var i = 0; i < entities.Count; i++)
            {
                if (clashes[i] is null)
                    restoring.Add(entities[i]);
                else
                    conflicts.Add(ctx.IdOf(entities[i]));
            }

            return (restoring, conflicts);
        }, recordType, ct);
    }

    /// <summary>
    /// Restores the rows <paramref name="pick"/> chooses, in one transaction, so that each duplicate
    /// group they belong to reads as it did before the delete. A user's delete of one record deleted
    /// every copy in its group at one <see cref="ISoftDeletable.DeletedAt"/>
    /// (<see cref="DuplicateDelete.EveryCopy"/>), and reads show a group only through its primary
    /// (<see cref="ReadVisibilityFilter.ExcludeNonPrimary{TEntity}"/>); restoring one non-primary copy
    /// alone would bring back a row no read shows, so a dose would not count in IOB/COB. The group's
    /// copies deleted at the restored row's instant come back with it, unless a live row holds a
    /// unique key one would take. A group still left without a live primary, because its primary was
    /// deleted by another delete, is repointed onto a live copy, whose <c>SysUpdatedAt</c> moves so
    /// a v3 history client is sent it.
    /// </summary>
    /// <remarks>
    /// Every restored row is saved through the change tracker, so each gets its own audit row and a
    /// fresh <c>SysUpdatedAt</c>.
    /// </remarks>
    /// <param name="ctx">The context the restore writes through.</param>
    /// <param name="pick">Loads the rows to restore, tracked, and the ids refused for a key clash.</param>
    /// <param name="recordType">The domain type named in a conflict's message.</param>
    /// <param name="ct">The cancellation token.</param>
    private static Task<GroupRestore<TEntity>> RestoreInGroupsAsync<TEntity>(
        this NocturneDbContext ctx,
        Func<CancellationToken, Task<(List<TEntity> Restoring, List<Guid> Conflicts)>> pick,
        string recordType,
        CancellationToken ct)
        where TEntity : class, ITenantScoped, ISoftDeletable
        => ctx.ExecuteInTransactionAsync(async token =>
        {
            var (restoring, conflicts) = await pick(token);
            var dedup = DuplicateGroupPrimaries.RecordTypeOf<TEntity>();

            List<TEntity> copies = [];
            if (dedup is { } type && restoring.Count > 0)
            {
                var candidates = await ctx.CoDeletedCopiesAsync(type, restoring, token);
                var clashes = await ctx.FindLiveKeyClashesAsync([.. restoring, .. candidates], token);
                copies = candidates.Where((_, i) => clashes[restoring.Count + i] is null).ToList();
            }

            foreach (var entity in restoring.Concat(copies))
                entity.DeletedAt = null;
            await ctx.SaveRestoreAsync(recordType, token);

            IReadOnlyList<Guid> promoted = dedup is { } restoredType && restoring.Count > 0
                ? await DuplicateGroupPrimaries.RepointHeadlessGroupsAsync(
                    ctx, restoredType, [.. restoring.Concat(copies).Select(ctx.IdOf)],
                    NocturneDbContext.UtcNowAtStoredPrecision(), token)
                : [];

            return new GroupRestore<TEntity>(restoring, conflicts, copies, promoted);
        }, ct: ct);

    /// <summary>
    /// The soft-deleted rows linked into the duplicate groups of <paramref name="restoring"/> whose
    /// <see cref="ISoftDeletable.DeletedAt"/> is that of a row being restored from the same group:
    /// the copies one delete took together.
    /// </summary>
    private static async Task<List<TEntity>> CoDeletedCopiesAsync<TEntity>(
        this NocturneDbContext ctx, RecordType recordType, IReadOnlyList<TEntity> restoring, CancellationToken ct)
        where TEntity : class, ITenantScoped, ISoftDeletable
    {
        var key = RecordTypeKeys.Key(recordType);
        var deletedAtById = restoring.ToDictionary(ctx.IdOf, e => e.DeletedAt);
        var ids = deletedAtById.Keys.ToArray();

        var ownLinks = await ctx.LinkedRecords.AsNoTracking()
            .Where(lr => lr.RecordType == key && ids.Contains(lr.RecordId))
            .Select(lr => new { lr.CanonicalId, lr.RecordId })
            .ToListAsync(ct);
        if (ownLinks.Count == 0)
            return [];

        var stampsByGroup = ownLinks
            .GroupBy(l => l.CanonicalId)
            .ToDictionary(g => g.Key, g => g.Select(l => deletedAtById[l.RecordId]).ToHashSet());
        var canonicals = stampsByGroup.Keys.ToArray();
        var mateLinks = await ctx.LinkedRecords.AsNoTracking()
            .Where(lr => lr.RecordType == key && canonicals.Contains(lr.CanonicalId) && !ids.Contains(lr.RecordId))
            .Select(lr => new { lr.CanonicalId, lr.RecordId })
            .ToListAsync(ct);
        if (mateLinks.Count == 0)
            return [];

        var mateIds = mateLinks.Select(l => l.RecordId).Distinct().ToArray();
        var groupsOf = mateLinks.ToLookup(l => l.RecordId, l => l.CanonicalId);
        var deletedMates = await ctx.DeletedRows<TEntity>()
            .Where(e => mateIds.Contains(EF.Property<Guid>(e, IdProperty)))
            .ToListAsync(ct);
        return deletedMates
            .Where(m => groupsOf[ctx.IdOf(m)].Any(c => stampsByGroup[c].Contains(m.DeletedAt)))
            .ToList();
    }

    private static Guid IdOf(this NocturneDbContext ctx, object entity)
        => (Guid)ctx.Entry(entity).Property(IdProperty).CurrentValue!;

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
