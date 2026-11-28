using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.Platform;

/// <summary>
/// What a clock face's owner may give anyone else now in the face's tenant: what the face may serve
/// to whoever holds its link, so a member whose access is taken away or narrowed cannot keep handing
/// it out through a face made earlier.
/// </summary>
/// <remarks>
/// The bound is the owner's direct reason (<see cref="GrantCeiling.Of"/>), as for every other site
/// that gives to others: access that comes only through a team is the owner's to read, not to put
/// on a link.
/// </remarks>
public static class ClockFaceOwnerScopes
{
    /// <summary>
    /// What the face's owner may give now in <paramref name="tenantId"/>, as a member signed in with
    /// no credential limit of their own; <see cref="GrantCeiling.Nothing"/> when the owner holds no
    /// direct reason there, is inactive, or has left.
    /// </summary>
    public static async Task<GrantCeiling> ResolveAsync(
        ITenantMemberService members, Guid tenantId, ClockFace face, CancellationToken ct)
    {
        if (!Guid.TryParse(face.UserId, out var owner)
            || await members.GetMembershipFactsAsync(owner, tenantId, ct) is not { } membership)
            return GrantCeiling.Nothing;

        var noCredentialLimit = new HashSet<string>();
        var sum = membership.Access;
        return GrantCeiling.Of(
                sum,
                membership.Direct,
                MemberScopeResolver.Resolve(sum.EffectivePermissions, AuthType.SessionCookie, noCredentialLimit),
                MemberScopeResolver.IsHistoryClamped(
                    credentialLimitTo24Hours: false, sum.LimitTo24Hours, sum.EffectivePermissions),
                AuthType.SessionCookie,
                noCredentialLimit,
                credentialLimitTo24Hours: false)
            ?? GrantCeiling.Nothing;
    }

    /// <summary>
    /// The owner's scopes, with the request's every read clamped to the last 24 hours when the
    /// owner's are: the link reads as its owner may give, never wider.
    /// </summary>
    public static async Task<IReadOnlySet<string>> ReadAsOwnerAsync(
        this HttpContext context, ITenantMemberService members, Guid tenantId, ClockFace face, CancellationToken ct)
    {
        var owner = await ResolveAsync(members, tenantId, face, ct);
        if (owner.HistoryClamped)
            context.ClampMemberHistory();
        return owner.Scopes;
    }

    /// <summary>
    /// The scopes a caller may put on a face: what they may give anyone else, since the face hands it
    /// to whoever holds the link. Empty for a caller whose access comes only from a team.
    /// </summary>
    public static IReadOnlySet<string> ClockFaceGivableScopes(this HttpContext context) =>
        context.CallerGrantCeiling()?.Scopes ?? new HashSet<string>();
}
