using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Middleware;

/// <summary>
/// An authenticated subject's membership of the resolved tenant, read once for the request and handed
/// from <see cref="AuthenticationMiddleware"/> to <see cref="MemberScopeMiddleware"/> through
/// <c>HttpContext.Items</c>.
/// </summary>
/// <remarks>
/// Each context open costs a <c>set_config</c> round trip, and every authenticated
/// request needs these facts before its controller runs. So they are read in one statement: joined to
/// the grant by a handler that already reads one (<see cref="IOAuthGrantRepository"/>), otherwise
/// joined to the subject's row with its platform-admin flag (<see cref="ReadAsync"/>). Nothing outlives
/// the request, so a removed member, an ended membership or a revoked credential is refused on the
/// next one.
/// </remarks>
/// <param name="SubjectId">The authenticated subject.</param>
/// <param name="TenantId">The resolved tenant.</param>
/// <param name="Membership">The subject's membership of the tenant, or null when they hold none.</param>
internal sealed record RequestMembership(Guid SubjectId, Guid TenantId, MembershipFacts? Membership)
{
    private const string ItemsKey = "RequestMembership";

    /// <summary>
    /// Whether the subject's row was read as inactive. Only <see cref="ReadAsync"/> reads the row; a
    /// grant handler's read finds only an active subject's grant
    /// (<see cref="IOAuthGrantRepository.FindLiveDirectGrantAsync(Guid, DirectGrantLookup, DateTime, CancellationToken)"/>).
    /// </summary>
    internal bool SubjectIsInactive { get; init; }

    /// <summary>Hands the membership to the rest of the request's pipeline.</summary>
    internal void StoreOn(HttpContext context) => context.Items[ItemsKey] = this;

    /// <summary>The membership stored on the request for this subject and tenant, if one was.</summary>
    internal static RequestMembership? Of(HttpContext context, Guid subjectId, Guid tenantId) =>
        context.Items[ItemsKey] is RequestMembership stored
        && stored.SubjectId == subjectId
        && stored.TenantId == tenantId
            ? stored
            : null;

    /// <summary>
    /// Reads the subject's membership of <paramref name="tenantId"/>, their platform-admin standing and
    /// their active state in one statement (<see cref="ITenantMembershipRepository.ReadSubjectMembershipAsync"/>).
    /// </summary>
    internal static async Task<(RequestMembership Membership, bool IsPlatformAdmin)> ReadAsync(
        ITenantMembershipRepository members, Guid subjectId, Guid tenantId, DateTime now, CancellationToken ct)
    {
        var read = await members.ReadSubjectMembershipAsync(subjectId, tenantId, now, ct);
        return (
            new RequestMembership(subjectId, tenantId, MembershipFacts.From(read?.Membership))
            {
                SubjectIsInactive = read is { IsActive: false },
            },
            read?.IsPlatformAdmin ?? false);
    }
}

/// <summary>A membership as <see cref="RequestMembership"/> reads it.</summary>
/// <param name="Id">The membership row.</param>
/// <param name="Access">What the membership grants on its tenant: the sum of its reasons.</param>
/// <param name="Direct">
/// What its direct reason grants, the bound of what the member gives anyone else
/// (<see cref="Extensions.HttpContextExtensions.CallerGrantCeiling"/>); null when the member holds no
/// direct reason in force.
/// </param>
/// <param name="LastUsedAt">When the membership was last stamped as used.</param>
internal sealed record MembershipFacts(Guid Id, TenantMemberAccess Access, TenantMemberAccess? Direct, DateTime? LastUsedAt)
{
    /// <summary>The membership a grant repository read beside the grant, or null when there was none.</summary>
    internal static MembershipFacts? From(DirectGrantMembership? membership) =>
        membership is null ? null : new MembershipFacts(membership.Id, membership.Access, membership.Direct, membership.LastUsedAt);

    /// <summary>The membership the membership repository read beside the subject, or null when there was none.</summary>
    internal static MembershipFacts? From(TenantMembershipFacts? membership) =>
        membership is null ? null : new MembershipFacts(membership.Id, membership.Access, membership.Direct, membership.LastUsedAt);
}
