using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Builds the cross-tenant caregiver overview: one item per tenant the subject
/// is an active member of with glucose read permission.
/// </summary>
/// <seealso cref="TenantOverviewService"/>
public interface ITenantOverviewService
{
    /// <summary>
    /// Returns the latest canonical reading, resolved thresholds, status classification,
    /// and active alert summary for every qualifying tenant of <paramref name="subjectId"/>.
    /// Per-tenant access is each membership's effective permissions intersected with
    /// <paramref name="tokenScopes"/> (the auth token's granted scopes), mirroring
    /// <c>MemberScopeMiddleware</c>: a superuser membership bypasses the intersection.
    /// </summary>
    Task<TenantOverviewResponse> GetOverviewAsync(
        Guid subjectId, IReadOnlySet<string> tokenScopes, CancellationToken ct = default);
}
