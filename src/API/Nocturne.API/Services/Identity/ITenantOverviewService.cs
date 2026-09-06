using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;

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
public sealed record GlucoseReadTenant(TenantEntity Tenant, IReadOnlySet<string> AllowedScopes);
