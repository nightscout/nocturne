using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Builds the cross-tenant caregiver overview: one item per tenant the subject
/// is an active member of with glucose read permission.
/// </summary>
/// <seealso cref="TenantOverviewService"/>
public interface ITenantOverviewService
{
    /// <summary>
    /// Returns one item for every qualifying tenant of <paramref name="subjectId"/>, in display
    /// order: tenants with a reason for attention first, most severe first, then by name.
    /// The alert summary and the therapy, device and tracker parts of an item are null when the
    /// caller's access to that tenant does not cover them.
    /// Per-tenant access is resolved by <see cref="MemberScopeResolver"/> from each membership's
    /// effective permissions, <paramref name="authType"/> and <paramref name="tokenScopes"/> — the
    /// same resolution <c>MemberScopeMiddleware</c> applies per request. A tenant whose
    /// membership, or <paramref name="credentialLimitTo24Hours"/>, limits the caller to the last
    /// 24 hours is read under that clamp. The flag rules <paramref name="rules"/> names are judged for each person
    /// (<see cref="IViewerFlagRules"/>), and their reasons merged into the person's. Only the people
    /// <paramref name="query"/> keeps are listed, in its order; an empty query keeps everyone in the order above.
    /// </summary>
    Task<TenantOverviewResponse> GetOverviewAsync(
        Guid subjectId, IReadOnlySet<string> tokenScopes, AuthType authType,
        OverviewRuleSet rules,
        PeopleQuery query,
        bool credentialLimitTo24Hours = false,
        CancellationToken ct = default);

    /// <summary>
    /// The groups the subject is in as current staff, for the People page's tabs
    /// (<see cref="ITeamRepository.GetGroupsOfStaffAsync"/>).
    /// </summary>
    Task<IReadOnlyList<StaffGroup>> GetGroupsAsync(Guid subjectId, CancellationToken ct);

    /// <summary>
    /// <see cref="GetOverviewAsync"/> for only the people of <paramref name="groupId"/> whose team's access has
    /// not ended, in the same order, with how many of them the subject cannot read
    /// (<see cref="TenantOverviewResponse.HiddenCount"/>). Null when the subject is not in the group as current
    /// staff.
    /// </summary>
    Task<TenantOverviewResponse?> GetGroupOverviewAsync(
        Guid subjectId, Guid groupId, IReadOnlySet<string> tokenScopes, AuthType authType,
        OverviewRuleSet rules, PeopleQuery query, bool credentialLimitTo24Hours, CancellationToken ct);

    /// <summary>
    /// <see cref="GetReviewAsync"/> for only the people of <paramref name="groupId"/> whose team's access has not
    /// ended. Null when the subject is not in the group as current staff.
    /// </summary>
    Task<TenantReviewResponse?> GetGroupReviewAsync(
        Guid subjectId, Guid groupId, IReadOnlySet<string> tokenScopes, AuthType authType, int days,
        OverviewRuleSet rules, PeopleQuery query, bool credentialLimitTo24Hours, CancellationToken ct);

    /// <summary>
    /// Returns the statistics of the last <paramref name="days"/> days, and the change in time in
    /// range from the period before, for every tenant <see cref="GetGlucoseReadTenantsAsync"/>
    /// returns. A tenant whose access lacks reports read, or whose membership or
    /// <paramref name="credentialLimitTo24Hours"/> limits the caller to the last 24 hours, is
    /// returned unavailable and read not at all. A period is the span ending now, as
    /// <c>GET /api/v4/statistics/periods</c> reads it. A caller with no subject of its own has no
    /// memberships, and gets the periods with no people. Only the people <paramref name="query"/> keeps are returned,
    /// judged as <see cref="GetOverviewAsync"/> judges them with <paramref name="rules"/>, so the two reads of one query
    /// list the same people.
    /// </summary>
    Task<TenantReviewResponse> GetReviewAsync(
        Guid? subjectId, IReadOnlySet<string> tokenScopes, AuthType authType, int days,
        OverviewRuleSet rules,
        PeopleQuery query,
        bool credentialLimitTo24Hours = false,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the last 24 hours of one person: the item <see cref="GetOverviewAsync"/> builds for
    /// <paramref name="tenantId"/>, its readings at one per 5-minute bucket, and its newest manual
    /// boluses and carb intakes. Null when <paramref name="tenantId"/> is not a tenant
    /// <see cref="GetGlucoseReadTenantsAsync"/> returns, whether it does not exist or the caller may not
    /// read it, and then nothing of the tenant is read. A caller with no subject of its own has no
    /// memberships, so it gets null. The read runs under the same history clamp as the overview's, and
    /// judges the flag rules <paramref name="rules"/> names as the overview does.
    /// </summary>
    Task<TenantPersonDetail?> GetPersonAsync(
        Guid? subjectId, Guid tenantId, IReadOnlySet<string> tokenScopes, AuthType authType,
        OverviewRuleSet rules,
        bool credentialLimitTo24Hours = false,
        CancellationToken ct = default);

    /// <summary>
    /// Returns every active tenant the subject may read glucose for: active memberships
    /// (revoked ones are excluded by the global query filter) whose scopes, as resolved by
    /// <see cref="MemberScopeResolver"/> from <paramref name="authType"/> and
    /// <paramref name="tokenScopes"/>, satisfy
    /// <see cref="Scope.GlucoseRead"/>. This is the authorization core shared by
    /// <see cref="GetOverviewAsync"/> and the overview hub.
    /// </summary>
    Task<IReadOnlyList<GlucoseReadTenant>> GetGlucoseReadTenantsAsync(
        Guid subjectId, IReadOnlySet<string> tokenScopes, AuthType authType,
        CancellationToken ct = default);
}

/// <summary>
/// A tenant the subject may read glucose for, with the scopes resolved for the
/// caller's credential on that tenant.
/// </summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="AllowedScopes">What the caller may see there.</param>
/// <param name="MembershipLimitTo24Hours">
/// The membership's own flag, before <see cref="MemberScopeResolver.IsHistoryClamped"/> combines it
/// with the credential's limit and the member's exemption.
/// </param>
/// <param name="EffectivePermissions">
/// The membership's <see cref="TenantMemberRecord.EffectivePermissions"/>.
/// </param>
public sealed record GlucoseReadTenant(
    TenantRecord Tenant,
    IReadOnlySet<string> AllowedScopes,
    bool MembershipLimitTo24Hours,
    IReadOnlySet<string> EffectivePermissions);
