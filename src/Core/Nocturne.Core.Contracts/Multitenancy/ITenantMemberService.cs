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
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the subject is a member of the tenant; otherwise <c>false</c>.</returns>
    Task<bool> IsMemberAsync(Guid subjectId, Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns all tenant identifiers that the specified subject belongs to.
    /// </summary>
    /// <param name="subjectId">The subject (user) identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of tenant identifiers the subject is a member of.</returns>
    Task<List<Guid>> GetTenantIdsForSubjectAsync(Guid subjectId, CancellationToken ct = default);

    /// <summary>
    /// Returns the number of members in the specified tenant.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The member count.</returns>
    Task<int> GetMemberCountAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns the names of the roles assigned to the specified subject within the given tenant.
    /// </summary>
    /// <param name="subjectId">The subject (user) identifier.</param>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The subject's tenant role names, or an empty list if they have none or are not a member.</returns>
    Task<List<string>> GetMemberRoleNamesAsync(Guid subjectId, Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns the membership's effective permissions — its role permissions unioned with its direct
    /// permissions — or null when the subject is not a member of the tenant.
    /// </summary>
    /// <remarks>
    /// The input to <c>MemberScopeResolver.Resolve</c>, for callers that resolve a membership outside
    /// the HTTP pipeline and so cannot rely on <c>MemberScopeMiddleware</c> having run. Null and
    /// empty are distinct: null is "not a member of this tenant", empty is "a member the tenant
    /// granted nothing".
    /// </remarks>
    /// <param name="subjectId">The subject (user) identifier.</param>
    /// <param name="tenantId">The tenant identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlySet<string>?> GetEffectivePermissionsAsync(
        Guid subjectId, Guid tenantId, CancellationToken ct = default);
}
