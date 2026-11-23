namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Service for checking tenant membership.
/// Used by auth handlers to verify a subject belongs to the resolved tenant.
/// </summary>
/// <seealso cref="ITenantAccessor"/>
/// <seealso cref="ITenantService"/>
public interface ITenantMemberService
{
    /// <summary>
    /// Checks whether the specified subject is a member of the given tenant.
    /// </summary>
    /// <param name="subjectId">The subject (user) identifier.</param>
    /// <returns><c>true</c> if the subject is a member of the tenant; otherwise <c>false</c>.</returns>
    Task<bool> IsMemberAsync(Guid subjectId, Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Whether the subject was let in to the tenant directly, by an invite, a request or a manager,
    /// and that access has not ended. Access through a team alone is not.
    /// </summary>
    Task<bool> IsDirectMemberAsync(Guid subjectId, Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Returns all tenant identifiers that the specified subject belongs to, including tenants whose
    /// membership has ended (<see cref="ITenantMembershipRepository.ListTenantIdsOfSubjectAsync"/>).
    /// </summary>
    /// <param name="subjectId">The subject (user) identifier.</param>
    Task<List<Guid>> GetTenantIdsForSubjectAsync(Guid subjectId, CancellationToken ct = default);

    /// <summary>
    /// Returns the names of the roles assigned to the specified subject within the given tenant.
    /// </summary>
    /// <param name="subjectId">The subject (user) identifier.</param>
    /// <returns>The subject's tenant role names, or an empty list if they have none or are not a member.</returns>
    Task<List<string>> GetMemberRoleNamesAsync(Guid subjectId, Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns what the subject's membership grants on the tenant, or null when the subject is not a
    /// member of it.
    /// </summary>
    /// <remarks>
    /// The input to <c>MemberScopeResolver.Resolve</c> and <c>MemberScopeResolver.IsHistoryClamped</c>,
    /// for callers that resolve a membership outside the HTTP pipeline and so cannot rely on
    /// <c>MemberScopeMiddleware</c> having run.
    /// </remarks>
    /// <param name="subjectId">The subject (user) identifier.</param>
    Task<TenantMemberAccess?> GetMemberAccessAsync(
        Guid subjectId, Guid tenantId, CancellationToken ct = default);
}

/// <summary>
/// What a membership grants on its tenant.
/// </summary>
/// <param name="EffectivePermissions">
/// Role permissions unioned with direct permissions. Empty is a member the tenant granted nothing,
/// which is distinct from not being a member at all.
/// </param>
/// <param name="LimitTo24Hours">The membership's own <c>limit_to_24_hours</c> flag, before any exemption.</param>
/// <param name="AccessEndsAt">When the membership ends, in UTC; null when it does not.</param>
public sealed record TenantMemberAccess(IReadOnlySet<string> EffectivePermissions, bool LimitTo24Hours, DateTime? AccessEndsAt);
