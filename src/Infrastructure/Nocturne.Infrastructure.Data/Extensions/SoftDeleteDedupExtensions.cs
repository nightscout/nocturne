using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Soft-delete dedup helpers for the re-import guard. Blocking is decided from the
/// <c>deleted_by_user</c> flag carried on each soft-deletable row (maintained by the
/// audit interceptor and the bulk-delete helpers), so it is a single index seek with
/// no audit-log scan or per-entity group-by. The rule itself is
/// <see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>.
/// </summary>
public static class SoftDeleteDedupExtensions
{
    private const string DeletedByUserProperty = "DeletedByUser";

    /// <summary>
    /// Narrows <paramref name="source"/> to the rows whose external identity must not be
    /// re-created: an active row, or a soft-deleted row whose latest delete was
    /// user-initiated (its <c>deleted_by_user</c> flag is set). A row swept by the system
    /// (<c>deleted_by_user = false</c>), and an identity with no row at all, do NOT block —
    /// resync produces a fresh row with a new <c>Id</c> and the prior soft-deleted row is
    /// left in place for audit continuity.
    ///
    /// Depends on connector-pipeline sweep deletes being wrapped in
    /// <c>SystemAuditScope</c> at the call site so their delete carries no auth
    /// context (<c>deleted_by_user = false</c>).
    ///
    /// <paramref name="source"/> must have the soft-delete query filter lifted
    /// (<c>IgnoreQueryFilters</c>) with its tenant predicate re-applied, or no soft-deleted
    /// row can reach the predicate.
    /// </summary>
    public static IQueryable<TEntity> WhereBlocksRecreation<TEntity>(this IQueryable<TEntity> source)
        where TEntity : class, ISoftDeletable
        => source.Where(e => e.DeletedAt == null || EF.Property<bool>(e, DeletedByUserProperty));

    /// <summary>
    /// Of the rows <see cref="WhereBlocksRecreation{TEntity}"/> kept for one external identity, the
    /// row that governs a re-upload of it: the live row when there is one — the write upserts that —
    /// otherwise the user tombstone, which blocks the write. The partial unique index counts live
    /// rows only, so a tombstone and a live row can share an identity.
    /// </summary>
    public static TEntity? GoverningRow<TEntity>(this IEnumerable<TEntity> rows)
        where TEntity : class, ISoftDeletable
    {
        TEntity? tombstone = null;
        foreach (var row in rows)
        {
            if (row.DeletedAt == null)
                return row;
            tombstone ??= row;
        }

        return tombstone;
    }

    /// <summary>
    /// Returns the subset of <paramref name="legacyIds"/> that must be skipped on bulk
    /// insert, per <see cref="WhereBlocksRecreation{TEntity}"/>.
    /// </summary>
    public static async Task<RecreationBlocks<string>> GetBlockingLegacyIdsAsync<TEntity>(
        this NocturneDbContext ctx,
        HashSet<string> legacyIds,
        CancellationToken ct = default)
        where TEntity : class, IV4Entity
    {
        if (legacyIds.Count == 0)
            return RecreationBlocks<string>.None;

        var blocking = await ctx.Set<TEntity>().IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == ctx.TenantId
                     && e.LegacyId != null
                     && legacyIds.Contains(e.LegacyId))
            .WhereBlocksRecreation()
            .Select(e => new { Key = e.LegacyId!, Live = e.DeletedAt == null })
            .ToListAsync(ct);

        return RecreationBlocks<string>.From(blocking.Select(b => (b.Key, b.Live)));
    }

    /// <summary>
    /// Sibling of <see cref="GetBlockingLegacyIdsAsync{TEntity}"/> for entities keyed
    /// by <c>CorrelationId</c> (Guid) instead of <c>LegacyId</c> (string). Currently
    /// used by <c>DeviceStatusExtrasEntity</c> only.
    /// </summary>
    public static async Task<RecreationBlocks<Guid>> GetBlockingCorrelationIdsAsync(
        this NocturneDbContext ctx,
        HashSet<Guid> correlationIds,
        CancellationToken ct = default)
    {
        if (correlationIds.Count == 0)
            return RecreationBlocks<Guid>.None;

        var blocking = await ctx.DeviceStatusExtras.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == ctx.TenantId
                     && correlationIds.Contains(e.CorrelationId))
            .WhereBlocksRecreation()
            .Select(e => new { Key = e.CorrelationId, Live = e.DeletedAt == null })
            .ToListAsync(ct);

        return RecreationBlocks<Guid>.From(blocking.Select(b => (b.Key, b.Live)));
    }
}

/// <summary>
/// The identities a bulk insert must skip, per
/// <see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>, split by what holds them.
/// </summary>
/// <param name="Held">Every blocked identity: skip these.</param>
/// <param name="DeletedByUser">
/// The identities in <paramref name="Held"/> held only by a row the user deleted. The rest are held by
/// a live row, so the record is already stored and skipping it loses nothing.
/// </param>
public sealed record RecreationBlocks<TKey>(IReadOnlySet<TKey> Held, IReadOnlySet<TKey> DeletedByUser)
    where TKey : notnull
{
    public static RecreationBlocks<TKey> None { get; } = new(new HashSet<TKey>(), new HashSet<TKey>());

    /// <summary>
    /// A tombstone and a live row can share an identity (see
    /// <see cref="SoftDeleteDedupExtensions.GoverningRow{TEntity}"/>), and the live row governs.
    /// </summary>
    public static RecreationBlocks<TKey> From(IEnumerable<(TKey Key, bool Live)> rows)
    {
        var held = new HashSet<TKey>();
        var live = new HashSet<TKey>();
        foreach (var (key, isLive) in rows)
        {
            held.Add(key);
            if (isLive)
                live.Add(key);
        }

        return new RecreationBlocks<TKey>(held, held.Except(live).ToHashSet());
    }
}
