using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// What an owner giving a team access, or changing what it gives, may give: the one rule the approval's
/// and the edit's transactions decide by and the link's offer is shown from.
/// </summary>
public static class TeamAccessRules
{
    /// <summary>
    /// The scopes the approver's membership of the site resolves to on their credential, and whether
    /// they read only the last 24 hours. Read from that membership and the credential's own limit,
    /// never from the request's tenant.
    /// </summary>
    public static (IReadOnlySet<string> Scopes, bool Limited) Approver(
        IEnumerable<string> effectivePermissions, bool membershipLimitTo24Hours, TeamAccessApproverCredential credential)
    {
        var effective = effectivePermissions.ToHashSet(StringComparer.Ordinal);
        return (
            MemberScopeResolver.Resolve(effective, credential.AuthType, credential.Scopes),
            MemberScopeResolver.IsHistoryClamped(credential.LimitTo24Hours, membershipLimitTo24Hours, effective));
    }

    /// <summary>
    /// Whether the caller <paramref name="role"/> was read for may give it to a team: they may confer it,
    /// and it holds permissions, all for data only.
    /// </summary>
    public static bool MayGiveToTeam(TenantRoleDto role) =>
        role.CanGrant && role.Permissions.Count > 0 && Scope.IsDataOnly(role.Permissions);

    /// <summary>
    /// Why <paramref name="grant"/>'s limit or end is wider than the link suggests or than the
    /// approver's own access (<see cref="Refusal(bool, DateTime?, bool, DateTime?, DateTime)"/>), or null
    /// when it is within both.
    /// </summary>
    public static TeamAccessRefusal? Refusal(
        TeamAccessSuggestion suggestion, bool approverLimited, DateTime? approverEnd, TeamAccessGrant grant, DateTime now) =>
        Refusal(suggestion.LimitTo24Hours || approverLimited, approverEnd, grant.LimitTo24Hours, grant.AccessEndsAt, now)
        ?? (suggestion.AccessEndsAt is { } latest && (grant.AccessEndsAt is not { } chosen || chosen > latest)
            ? TeamAccessRefusal.EndLaterThanLink
            : null);

    /// <summary>
    /// Why an access limited by <paramref name="limitTo24Hours"/> and ending at <paramref name="accessEndsAt"/>
    /// may not be given: it has ended, it lacks the 24-hour limit <paramref name="limitRequired"/> requires,
    /// or it outlasts the approver's own access, which ends at <paramref name="approverEnd"/>, null when it
    /// does not. Null when it may.
    /// </summary>
    public static TeamAccessRefusal? Refusal(
        bool limitRequired, DateTime? approverEnd, bool limitTo24Hours, DateTime? accessEndsAt, DateTime now)
    {
        if (accessEndsAt is { } end && MembershipLiveness.HasEnded(end, now))
            return TeamAccessRefusal.EndInPast;
        if (limitRequired && !limitTo24Hours)
            return TeamAccessRefusal.LimitRequired;
        if (MembershipLiveness.Outlasts(approverEnd, accessEndsAt))
            return TeamAccessRefusal.EndOutlastsApprover;
        return null;
    }
}
