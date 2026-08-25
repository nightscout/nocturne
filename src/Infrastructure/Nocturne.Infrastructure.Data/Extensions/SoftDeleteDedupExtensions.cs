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
    /// Returns the subset of <paramref name="legacyIds"/> that must be skipped on bulk
    /// insert, per <see cref="WhereBlocksRecreation{TEntity}"/>.
    /// </summary>
    public static async Task<HashSet<string>> GetBlockingLegacyIdsAsync<TEntity>(
        this NocturneDbContext ctx,
        HashSet<string> legacyIds,
        CancellationToken ct = default)
        where TEntity : class, IV4Entity
    {
        if (legacyIds.Count == 0)
            return new HashSet<string>();

        var blocking = await ctx.Set<TEntity>().IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == ctx.TenantId
                     && e.LegacyId != null
                     && legacyIds.Contains(e.LegacyId))
            .WhereBlocksRecreation()
            .Select(e => e.LegacyId!)
            .ToListAsync(ct);

        return blocking.ToHashSet();
    }

    /// <summary>
    /// Sibling of <see cref="GetBlockingLegacyIdsAsync{TEntity}"/> for entities keyed
    /// by <c>CorrelationId</c> (Guid) instead of <c>LegacyId</c> (string). Currently
    /// used by <c>DeviceStatusExtrasEntity</c> only.
    /// </summary>
    public static async Task<HashSet<Guid>> GetBlockingCorrelationIdsAsync(
        this NocturneDbContext ctx,
        HashSet<Guid> correlationIds,
        CancellationToken ct = default)
    {
        if (correlationIds.Count == 0)
            return new HashSet<Guid>();

        var blocking = await ctx.DeviceStatusExtras.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == ctx.TenantId
                     && correlationIds.Contains(e.CorrelationId))
            .WhereBlocksRecreation()
            .Select(e => e.CorrelationId)
            .ToListAsync(ct);

        return blocking.ToHashSet();
    }
}
