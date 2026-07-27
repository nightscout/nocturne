using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

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
    /// Per-tenant access is resolved by <see cref="MemberScopeResolver"/> from each membership's
    /// effective permissions, <paramref name="authType"/> and <paramref name="tokenScopes"/> — the
    /// same resolution <c>MemberScopeMiddleware</c> applies per request.
    /// </summary>
    Task<TenantOverviewResponse> GetOverviewAsync(
        Guid subjectId, IReadOnlySet<string> tokenScopes, AuthType authType,
        CancellationToken ct = default);
}
