using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The subject a tenant's API tokens are issued to.
/// </summary>
/// <remarks>
/// A device token belongs to the site, not to a person. Issuing it to a human subject makes it
/// inherit things that subject is rather than things it was granted: an owner who is also the
/// instance's platform admin would hand every uploader token instance-wide reach, and a token
/// outlives the person, so an ownership change or a revoked membership stops every device at once.
/// <para>
/// A system subject is the existing answer to "authenticated but stands for no one", as the
/// Public subject a share link runs as already is. It is excluded from
/// <see cref="OrphanedSubjectFilter"/>, so holding a membership without a passkey does not read as
/// an account locked out of the tenant, and it is never a platform admin.
/// </para>
/// <para>
/// Its membership carries <see cref="Scope.FullAccess"/> because the bound on a device token is the
/// token's own scope list: <c>MemberScopeMiddleware</c> intersects the two, so a narrower membership
/// here would silently cap every token rather than the one that deserved it. The subject cannot sign
/// in, so the membership is reachable only through a grant.
/// </para>
/// </remarks>
public static class DeviceSubjectFilter
{
    /// <summary>The name the device-holding system subject carries on every tenant.</summary>
    public const string DeviceSubjectName = "Devices";

    /// <summary>
    /// The holder's notes text. <c>MoveSubjectTokensToDirectGrants</c> writes the same words when it
    /// creates a holder in SQL; a migration is frozen once shipped, so changing this reaches only
    /// holders created from here on.
    /// </summary>
    public const string DeviceSubjectNotes =
        "Holds the API tokens for this site. The scopes on each token decide what it can do.";

    /// <summary>
    /// The subject <paramref name="tenantId"/> issues API tokens to, or null when it has never
    /// issued one.
    /// </summary>
    /// <param name="db">A context pinned to <paramref name="tenantId"/>.</param>
    /// <param name="tenantId">The tenant to resolve the holder for.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<Guid?> FindDeviceSubjectOf(
        this NocturneDbContext db, Guid tenantId, CancellationToken ct = default)
    {
        var existing = await db.TenantMembers
            .Where(m => m.TenantId == tenantId)
            .Select(m => m.Subject!)
            .Where(s => s.IsSystemSubject && s.Name == DeviceSubjectName)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);

        return existing == Guid.Empty ? null : existing;
    }

    /// <summary>
    /// The subject <paramref name="tenantId"/> issues API tokens to, creating it and its membership
    /// on first use. Only for a caller that is about to issue one: reading the token list must not
    /// write rows into a tenant that has never had a token.
    /// </summary>
    /// <param name="db">A context pinned to <paramref name="tenantId"/>.</param>
    /// <param name="tenantId">The tenant to resolve the holder for.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<Guid> DeviceSubjectOf(
        this NocturneDbContext db, Guid tenantId, CancellationToken ct = default)
    {
        if (await db.FindDeviceSubjectOf(tenantId, ct) is { } existing)
        {
            return existing;
        }

        var subjectId = Guid.CreateVersion7();

        db.Subjects.Add(new SubjectEntity
        {
            Id = subjectId,
            Name = DeviceSubjectName,
            Notes = DeviceSubjectNotes,
            IsActive = true,
            IsSystemSubject = true,
            ApprovalStatus = "Approved",
        });

        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SubjectId = subjectId,
            DirectPermissions = [Scope.FullAccess],
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync(ct);
        return subjectId;
    }
}
