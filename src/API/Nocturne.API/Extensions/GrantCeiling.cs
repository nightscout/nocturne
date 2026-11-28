using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Extensions;

/// <summary>
/// What a caller may give anyone else (<see cref="HttpContextExtensions.CallerGrantCeiling"/>).
/// </summary>
/// <param name="Scopes">The scopes the caller may confer.</param>
/// <param name="AccessEndsAt">When the caller's access to give ends, in UTC; null when it does not.</param>
/// <param name="HistoryClamped">Whether what the caller gives must read only the last 24 hours.</param>
public sealed record GrantCeiling(IReadOnlySet<string> Scopes, DateTime? AccessEndsAt, bool HistoryClamped)
{
    /// <summary>
    /// The ceiling of a caller with no direct reason, for a read that shows what the caller may give;
    /// a write refuses such a caller outright. It confers no scope, so every flag of what the caller
    /// may give reads false. Its end is none, so a read that marks what outlasts the caller marks
    /// nothing: <c>RoleController.GetRoles</c> shows no role as held past such a caller.
    /// </summary>
    public static readonly GrantCeiling Nothing = new(new HashSet<string>(), null, true);

    /// <summary>
    /// What a member may give anyone else: what their direct reason grants, never wider than the sum
    /// of their reasons; null when they hold no direct reason in force.
    /// </summary>
    /// <remarks>
    /// Never wider than the sum, because a row an older build narrowed can hold less than its direct
    /// reason.
    /// </remarks>
    /// <param name="sum">What the membership grants: the sum of its reasons.</param>
    /// <param name="direct">What its direct reason grants; null when it holds none in force.</param>
    /// <param name="sumScopes"><paramref name="sum"/> resolved for the credential (<see cref="MemberScopeResolver.Resolve"/>).</param>
    /// <param name="sumClamped">Whether <paramref name="sum"/> reads only the last 24 hours (<see cref="MemberScopeResolver.IsHistoryClamped"/>).</param>
    /// <param name="authType">How the member is signed in.</param>
    /// <param name="credentialScopes">The credential's own scopes.</param>
    /// <param name="credentialLimitTo24Hours">The credential's own 24-hour flag.</param>
    public static GrantCeiling? Of(
        TenantMemberAccess sum,
        TenantMemberAccess? direct,
        IReadOnlySet<string> sumScopes,
        bool sumClamped,
        AuthType authType,
        IReadOnlySet<string> credentialScopes,
        bool credentialLimitTo24Hours) =>
        direct switch
        {
            null => null,
            _ when ReferenceEquals(direct, sum) => new GrantCeiling(sumScopes, sum.AccessEndsAt, sumClamped),
            _ => new GrantCeiling(
                Scope.Common(MemberScopeResolver.Resolve(direct.EffectivePermissions, authType, credentialScopes), sumScopes),
                HttpContextExtensions.Earlier(direct.AccessEndsAt, sum.AccessEndsAt),
                sumClamped || MemberScopeResolver.IsHistoryClamped(
                    credentialLimitTo24Hours, direct.LimitTo24Hours, direct.EffectivePermissions)),
        };
}
