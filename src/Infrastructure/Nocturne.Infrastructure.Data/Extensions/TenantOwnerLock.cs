using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The tenant's owner lock (<see cref="AdvisoryLockExtensions"/>): what every write that can leave
/// a tenant with no owner holds from its last-owner check to its commit.
/// </summary>
/// <remarks>
/// Two writes that each find another owner before either commits leave the tenant with none, and
/// nothing inside it can repair that. A write that spans tenants takes the locks in ascending tenant
/// id, so two of them cannot each hold what the other waits for.
/// </remarks>
internal static class TenantOwnerLock
{
    internal const int Class = 0x4E4F_574E;

    internal static Task LockTenantOwnersAsync(this NocturneDbContext db, Guid tenantId, CancellationToken ct) =>
        db.LockForTransactionAsync(Class, tenantId, ct);

    private static async Task LockSubjectRowAsync(this NocturneDbContext db, Guid subjectId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
            return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A row lock is taken inside a transaction");

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM subjects WHERE id = {subjectId} FOR UPDATE", ct);
    }

    /// <summary>
    /// Takes the owner lock of every tenant the subject belongs to, then returns those whose owner the
    /// subject is and where no other owner who can sign in would remain
    /// (<see cref="TenantOwnerFilter.IsLastOwnerAsync"/>). The check reads after the locks, so it sees
    /// every write that held one first.
    /// </summary>
    /// <remarks>
    /// Locks every membership, not only an owner's: a role change that makes the subject an owner
    /// of a tenant takes that tenant's lock, so it either lands before the check or waits for the commit.
    /// The subject row is locked <c>FOR UPDATE</c> before the memberships are read: a membership insert
    /// takes <c>FOR KEY SHARE</c> on it through its foreign key, which only <c>FOR UPDATE</c> conflicts
    /// with, so every add of a member, owner or not, either commits before the read or waits for the
    /// commit.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    internal static async Task<IReadOnlyList<LastOwnerTenant>> LockTenantsOfSubjectAsync(
        this NocturneDbContext db, Guid subjectId, CancellationToken ct)
    {
        await db.LockSubjectRowAsync(subjectId, ct);

        var memberships = await db.TenantMembers.AsNoTracking()
            .Where(m => m.SubjectId == subjectId)
            .OrderBy(m => m.TenantId)
            .Select(m => new { m.Id, m.TenantId })
            .ToListAsync(ct);
        foreach (var membership in memberships)
            await db.LockTenantOwnersAsync(membership.TenantId, ct);

        var lastOwnerOf = new List<Guid>();
        foreach (var membership in memberships)
        {
            if (await db.TenantMembers.AsNoTracking().IsLastOwnerAsync(membership.TenantId, membership.Id, ct))
                lastOwnerOf.Add(membership.TenantId);
        }

        if (lastOwnerOf.Count == 0)
            return [];

        var slugs = await db.Tenants.AsNoTracking()
            .Where(t => lastOwnerOf.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Slug, ct);
        return lastOwnerOf.Select(id => new LastOwnerTenant(id, slugs.GetValueOrDefault(id, string.Empty))).ToList();
    }
}
