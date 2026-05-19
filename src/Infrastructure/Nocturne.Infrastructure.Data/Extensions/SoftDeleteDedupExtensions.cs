using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;

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

        // Step 2: latest "delete" audit row per soft-deleted entity.
        // Materialize first then group in memory — the EF in-memory provider
        // can't translate GroupBy + OrderByDescending + First().AuthType, and
        // on real Postgres this set is bounded by the dedup batch size, so the
        // round-trip cost is dominated by the index seek added in Task 2.
        var entityType = typeof(TEntity).Name.Replace("Entity", "");
        var softDeletedIds = softDeletedById.Keys.ToHashSet();

        var rawAudits = await ctx.MutationAuditLog
            .Where(a => a.EntityType == entityType
                     && softDeletedIds.Contains(a.EntityId)
                     && a.Action == "delete")
            .Select(a => new { a.EntityId, a.AuthType, a.CreatedAt })
            .ToListAsync(ct);

        var latestDeletes = rawAudits
            .GroupBy(a => a.EntityId)
            .Select(g => g.OrderByDescending(a => a.CreatedAt).First());

        foreach (var d in latestDeletes)
        {
            if (d.AuthType != null && softDeletedById.TryGetValue(d.EntityId, out var legacyId))
                blocking.Add(legacyId);
        }

        return blocking;
    }
}
