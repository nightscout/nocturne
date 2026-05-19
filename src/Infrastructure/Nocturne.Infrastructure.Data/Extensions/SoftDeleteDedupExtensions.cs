using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Audit-aware soft-delete dedup helper for V4 bulk-create paths. The two-step
/// query shape avoids EF Core's LATERAL/OUTER APPLY translation lottery and gives
/// predictable SQL even on large legacy_id batches.
/// </summary>
public static class SoftDeleteDedupExtensions
{
    /// <summary>
    /// Returns the subset of <paramref name="legacyIds"/> that must be skipped on
    /// bulk insert. A legacy_id is blocking if either:
    ///   - an active row exists with that legacy_id, or
    ///   - a soft-deleted row exists whose latest <c>delete</c> audit row has a
    ///     non-null <see cref="MutationAuditLogEntity.AuthType"/> (i.e. an HTTP
    ///     request populated the audit context, marking the delete as
    ///     user/guest-initiated).
    /// Soft-deleted rows whose latest <c>delete</c> audit row has
    /// <c>AuthType IS NULL</c> — or rows with no audit row at all (pre-audit
    /// legacy data) — do NOT block. Resync produces a fresh row with a new
    /// <c>Id</c>; the prior soft-deleted row is left in place for audit continuity.
    ///
    /// Depends on connector-pipeline sweep deletes being wrapped in
    /// <c>SystemAuditScope</c> at the call site so their audit rows carry
    /// <c>AuthType IS NULL</c>. Depends on the audit-config retention validator
    /// keeping <c>MutationAuditRetentionDays &gt;= SoftDeleteRetentionDays</c>
    /// so user-delete audit rows outlive their soft-deleted entities.
    /// </summary>
    public static async Task<HashSet<string>> GetBlockingLegacyIdsAsync<TEntity>(
        this NocturneDbContext ctx,
        HashSet<string> legacyIds,
        CancellationToken ct = default)
        where TEntity : class, IV4Entity
    {
        if (legacyIds.Count == 0)
            return new HashSet<string>();

        // Step 1: existing rows by legacy_id (ignore soft-delete filter)
        var existing = await ctx.Set<TEntity>().IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == ctx.TenantId
                     && e.LegacyId != null
                     && legacyIds.Contains(e.LegacyId))
            .Select(e => new { e.Id, e.LegacyId, e.DeletedAt })
            .ToListAsync(ct);

        if (existing.Count == 0)
            return new HashSet<string>();

        var blocking = new HashSet<string>();
        var softDeletedById = new Dictionary<Guid, string>();

        foreach (var row in existing)
        {
            if (row.DeletedAt == null)
                blocking.Add(row.LegacyId!);
            else
                softDeletedById[row.Id] = row.LegacyId!;
        }

        if (softDeletedById.Count == 0)
            return blocking;

        var entityType = typeof(TEntity).Name.Replace("Entity", "");
        var userDeletedIds = await GetUserDeletedEntityIdsAsync(
            ctx, entityType, softDeletedById.Keys.ToHashSet(), ct);

        foreach (var userId in userDeletedIds)
        {
            if (softDeletedById.TryGetValue(userId, out var legacyId))
                blocking.Add(legacyId);
        }

        return blocking;
    }

    /// <summary>
    /// Sibling of <see cref="GetBlockingLegacyIdsAsync{TEntity}"/> for entities keyed
    /// by <c>CorrelationId</c> (Guid) instead of <c>LegacyId</c> (string). Currently
    /// used by <c>DeviceStatusExtrasEntity</c> only. Same discrimination semantics
    /// otherwise — active rows always block, soft-deleted rows block iff the latest
    /// delete audit row has <c>AuthType IS NOT NULL</c>.
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
            .Select(e => new { e.Id, e.CorrelationId, e.DeletedAt })
            .ToListAsync(ct);

        if (existing.Count == 0)
            return new HashSet<Guid>();

        var blocking = new HashSet<Guid>();
        var softDeletedById = new Dictionary<Guid, Guid>();

        foreach (var row in existing)
        {
            if (row.DeletedAt == null)
                blocking.Add(row.CorrelationId);
            else
                softDeletedById[row.Id] = row.CorrelationId;
        }

        if (softDeletedById.Count == 0)
            return blocking;

        var userDeletedIds = await GetUserDeletedEntityIdsAsync(
            ctx, "DeviceStatusExtras", softDeletedById.Keys.ToHashSet(), ct);

        foreach (var userId in userDeletedIds)
        {
            if (softDeletedById.TryGetValue(userId, out var corrId))
                blocking.Add(corrId);
        }

        return blocking;
    }

    // Latest "delete" audit row per soft-deleted entity, filtered to user-attributed.
    // Materialize-first then group in memory — the EF in-memory provider can't
    // translate GroupBy + OrderByDescending + First().AuthType, and on real
    // Postgres this set is bounded by the dedup batch size, so the round-trip
    // cost is dominated by the index seek added in Task 2.
    private static async Task<HashSet<Guid>> GetUserDeletedEntityIdsAsync(
        NocturneDbContext ctx,
        string entityType,
        HashSet<Guid> softDeletedIds,
        CancellationToken ct)
    {
        if (softDeletedIds.Count == 0)
            return new HashSet<Guid>();

        var raw = await ctx.MutationAuditLog
            .Where(a => a.EntityType == entityType
                     && softDeletedIds.Contains(a.EntityId)
                     && a.Action == "delete")
            .Select(a => new { a.EntityId, a.AuthType, a.CreatedAt })
            .ToListAsync(ct);

        return raw
            .GroupBy(a => a.EntityId)
            .Select(g => g.OrderByDescending(a => a.CreatedAt).First())
            .Where(d => d.AuthType != null)
            .Select(d => d.EntityId)
            .ToHashSet();
    }
}
