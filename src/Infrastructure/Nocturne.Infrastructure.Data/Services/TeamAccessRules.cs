using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// What an owner approving a team's access may give: the one rule the approval's transaction decides
/// by and the link's offer is shown from.
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
    /// Why <paramref name="grant"/>'s limit or end is wider than the link suggests or than the
    /// approver's own access, or null when it is within both. <paramref name="approverLimited"/> is
    /// whether the approver reads only the last 24 hours, and <paramref name="approverEnd"/> when their
    /// own access ends, null when it does not.
    /// </summary>
    public static TeamAccessRefusal? Refusal(
        TeamAccessSuggestion suggestion, bool approverLimited, DateTime? approverEnd, TeamAccessGrant grant, DateTime now)
    {
        if (grant.AccessEndsAt is { } end && MembershipLiveness.HasEnded(end, now))
            return TeamAccessRefusal.EndInPast;
        if ((suggestion.LimitTo24Hours || approverLimited) && !grant.LimitTo24Hours)
            return TeamAccessRefusal.LimitRequired;
        if (suggestion.AccessEndsAt is { } latest && (grant.AccessEndsAt is not { } chosen || chosen > latest))
            return TeamAccessRefusal.EndLaterThanLink;
        if (MembershipLiveness.Outlasts(approverEnd, grant.AccessEndsAt))
            return TeamAccessRefusal.EndOutlastsApprover;
        return null;
    }
}
