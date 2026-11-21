using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.Auth;

/// <summary>Who may restore access for a member with no sign-in method left.</summary>
public enum RecoveryEligibility
{
    /// <summary>
    /// The member can still sign in, a recovery link by email can reach them, or they are not a
    /// member of the tenant (<see cref="OrphanedSubjectFilter"/>).
    /// </summary>
    NotLockedOut,

    /// <summary>
    /// Locked out, but the account reaches beyond the tenant (an instance administrator, or a
    /// member of another tenant), so only the operator may recover it.
    /// </summary>
    OperatorOnly,

    /// <summary>Locked out, and a member administrator of the tenant may recover it.</summary>
    Eligible,
}

/// <summary>
/// The one rule for whether a recovery code may be issued and, because the code is a bearer link
/// that outlives the moment it was minted, whether it may still be redeemed.
/// </summary>
/// <remarks>
/// A member administrator's authority is checked against this tenant's permissions, while the
/// account a passkey is enrolled onto is global, hence <see cref="RecoveryEligibility.OperatorOnly"/>.
/// </remarks>
public sealed class MemberRecoveryEligibility(ITenantMemberService tenantMembers, IAccountLinkRoute linkRoute)
{
    /// <param name="db">A context on <paramref name="tenantId"/> that is not share-flagged
    /// (<see cref="OrphanedSubjectFilter.OrphanedSubjectsOf"/>).</param>
    /// <param name="tenantId">The tenant the member is checked as locked out of.</param>
    /// <param name="subjectId">The member whose eligibility is checked.</param>
    /// <param name="ct">Cancels the operation.</param>
    public async Task<RecoveryEligibility> CheckAsync(
        NocturneDbContext db, Guid tenantId, Guid subjectId, CancellationToken ct = default)
    {
        var orphan = await db.OrphanedSubjectsOf(tenantId, linkRoute)
            .Where(s => s.Id == subjectId)
            .Select(s => new { s.IsPlatformAdmin })
            .FirstOrDefaultAsync(ct);

        if (orphan is null)
            return RecoveryEligibility.NotLockedOut;

        return await IsOperatorOnlyAsync(tenantId, subjectId, orphan.IsPlatformAdmin, ct)
            ? RecoveryEligibility.OperatorOnly
            : RecoveryEligibility.Eligible;
    }

    /// <summary>The members of <paramref name="tenantId"/> who cannot sign in (<see cref="OrphanedSubjectFilter"/>), by name.</summary>
    /// <param name="db">A context on <paramref name="tenantId"/> that is not share-flagged.</param>
    /// <param name="tenantId">The tenant whose members are considered.</param>
    /// <param name="ct">Cancels the read.</param>
    public async Task<IReadOnlyList<LockedOutSubject>> ListLockedOutAsync(
        NocturneDbContext db, Guid tenantId, CancellationToken ct = default) =>
        await db.OrphanedSubjectsOf(tenantId, linkRoute)
            .OrderBy(s => s.Name)
            .Select(s => new LockedOutSubject(s.Id, s.Name, s.Username, s.IsPlatformAdmin))
            .ToListAsync(ct);

    /// <summary>
    /// Whether a code of <paramref name="purpose"/> may still be spent on a member in
    /// <paramref name="eligibility"/>: a member administrator's code only while the member is
    /// <see cref="RecoveryEligibility.Eligible"/>, the operator's while they are locked out at all.
    /// </summary>
    public static bool AllowsRedemption(RecoveryEligibility eligibility, LoginCodePurpose purpose) =>
        eligibility == RecoveryEligibility.Eligible
        || (eligibility == RecoveryEligibility.OperatorOnly && purpose == LoginCodePurpose.OperatorRecovery);

    /// <summary>Whether the account is an instance administrator or a member of another tenant.</summary>
    public async Task<bool> IsOperatorOnlyAsync(
        Guid tenantId, Guid subjectId, bool isPlatformAdmin, CancellationToken ct = default) =>
        isPlatformAdmin
        || (await tenantMembers.GetTenantIdsForSubjectAsync(subjectId, ct)).Any(t => t != tenantId);
}

/// <summary>A member who cannot sign in, as <see cref="MemberRecoveryEligibility.ListLockedOutAsync"/> lists them.</summary>
public sealed record LockedOutSubject(Guid Id, string Name, string? Username, bool IsPlatformAdmin);
