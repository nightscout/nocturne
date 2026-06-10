using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Soft-delete dedup helper for V4 bulk-create paths. Blocking is decided from the
/// <c>deleted_by_user</c> flag carried on each soft-deletable row (maintained by the
/// audit interceptor and the bulk-delete helpers), so it is a single index seek with
/// no audit-log scan or per-entity group-by.
/// </summary>
public static class SoftDeleteDedupExtensions
{
    /// <summary>
    /// Returns the subset of <paramref name="legacyIds"/> that must be skipped on
    /// bulk insert. A legacy_id is blocking if either:
    ///   - an active row exists with that legacy_id, or
    ///   - a soft-deleted row exists whose latest delete was user-initiated
    ///     (its <c>deleted_by_user</c> flag is set).
    /// Soft-deleted rows deleted by a system sweep (<c>deleted_by_user = false</c>),
    /// and rows with no row at all (already hard-deleted / never existed), do NOT
    /// block. Resync produces a fresh row with a new <c>Id</c>; the prior soft-deleted
    /// row is left in place for audit continuity.
    ///
    /// Depends on connector-pipeline sweep deletes being wrapped in
    /// <c>SystemAuditScope</c> at the call site so their delete carries no auth
    /// context (<c>deleted_by_user = false</c>).
    /// </summary>
    public static async Task<HashSet<string>> GetBlockingLegacyIdsAsync<TEntity>(
        this NocturneDbContext ctx,
        HashSet<string> legacyIds,
        CancellationToken ct = default)
        where TEntity : class, IV4Entity
    {
        if (legacyIds.Count == 0)
            return new HashSet<string>();

        var existing = await ctx.Set<TEntity>().IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == ctx.TenantId
                     && e.LegacyId != null
                     && legacyIds.Contains(e.LegacyId))
            .Select(e => new
            {
                e.LegacyId,
                e.DeletedAt,
                DeletedByUser = EF.Property<bool>(e, "DeletedByUser")
            })
            .ToListAsync(ct);

        var blocking = new HashSet<string>();
        foreach (var row in existing)
        {
            // Active rows always block; a soft-deleted row blocks only when its latest
            // delete was user-initiated (system-sweep deletes stay re-creatable on resync).
            if (row.DeletedAt == null || row.DeletedByUser)
                blocking.Add(row.LegacyId!);
        }

        return blocking;
    }

    /// <summary>
    /// Sibling of <see cref="GetBlockingLegacyIdsAsync{TEntity}"/> for entities keyed
    /// by <c>CorrelationId</c> (Guid) instead of <c>LegacyId</c> (string). Currently
    /// used by <c>DeviceStatusExtrasEntity</c> only. Same discrimination semantics
    /// otherwise — active rows always block, soft-deleted rows block iff their latest
    /// delete was user-initiated (<c>deleted_by_user</c> is set).
    /// </summary>
    public static async Task<HashSet<Guid>> GetBlockingCorrelationIdsAsync(
        this NocturneDbContext ctx,
        HashSet<Guid> correlationIds,
        CancellationToken ct = default)
    {
        if (correlationIds.Count == 0)
            return new HashSet<Guid>();

        var existing = await ctx.DeviceStatusExtras.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == ctx.TenantId
                     && correlationIds.Contains(e.CorrelationId))
            .Select(e => new
            {
                e.CorrelationId,
                e.DeletedAt,
                DeletedByUser = EF.Property<bool>(e, "DeletedByUser")
            })
            .ToListAsync(ct);

        var blocking = new HashSet<Guid>();
        foreach (var row in existing)
        {
            if (row.DeletedAt == null || row.DeletedByUser)
                blocking.Add(row.CorrelationId);
        }

        return blocking;
    }
}
