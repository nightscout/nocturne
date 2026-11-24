using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
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

    /// <summary>
    /// Holds the subject's row <c>FOR UPDATE</c> to the commit, against every write that inserts a row
    /// naming the subject (<see cref="LockSubjectRowSharedAsync"/>): what account deletion takes before
    /// any other lock (<see cref="MembershipLock"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    internal static async Task LockSubjectRowAsync(this NocturneDbContext db, Guid subjectId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
            return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A row lock is taken inside a transaction");

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM subjects WHERE id = {subjectId} FOR UPDATE", ct);
    }

    /// <summary>
    /// Holds the subject's row against its deletion and against the <c>FOR UPDATE</c> that
    /// <see cref="LockTenantsOfSubjectAsync"/> takes, to the commit: what a write that inserts a row
    /// naming the subject takes first, so it orders ahead of the membership lock like those paths do
    /// (<see cref="MembershipLock"/>). Takes the lock the row's foreign keys would take later, and does
    /// not conflict with another <c>KEY SHARE</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    internal static async Task LockSubjectRowSharedAsync(this NocturneDbContext db, Guid subjectId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
            return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A row lock is taken inside a transaction");

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM subjects WHERE id = {subjectId} FOR KEY SHARE", ct);
    }

    /// <summary>
    /// <see cref="LockSubjectRowSharedAsync"/> for each of <paramref name="subjectIds"/>, in ascending id, so
    /// two writes that each lock several cannot each hold a row the other waits for.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    internal static async Task LockSubjectRowsSharedAsync(
        this NocturneDbContext db, IEnumerable<Guid> subjectIds, CancellationToken ct)
    {
        foreach (var subjectId in subjectIds.Distinct().Order())
            await db.LockSubjectRowSharedAsync(subjectId, ct);
    }

    /// <summary>
    /// Takes the owner lock of every tenant the subject belongs to, then returns those whose last
    /// owner the subject is (<see cref="TenantOwnerFilter.IsLastOwnerAsync"/>). The check reads after the locks, so it sees
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
        this NocturneDbContext db, Guid subjectId, IAccountLinkRoute linkRoute, CancellationToken ct)
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
            if (await db.IsLastOwnerAsync(membership.TenantId, membership.Id, linkRoute, ct))
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
