namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Resolved tenant information for the current request. Set on <see cref="ITenantAccessor"/>
/// by the tenant-resolution middleware and consumed by the DbContext for RLS enforcement.
/// </summary>
/// <param name="TenantId">The unique identifier of the resolved tenant.</param>
/// <param name="Slug">The URL-safe slug used to identify the tenant in routes.</param>
/// <param name="DisplayName">The human-readable display name of the tenant.</param>
/// <param name="IsActive">Whether the tenant is currently active and accepting data.</param>
/// <param name="IsDemo">
/// Whether this tenant is the deployment's demo. Required rather than defaulted: it gates
/// <c>GET /api/v4/demo/session</c>, which hands any anonymous caller a real member session, and
/// the <c>isDemo</c> field the login page reads to decide whether to sign a visitor in without
/// asking. Defaulting it let a construction site drop the flag silently — including one that
/// rebuilt a context from another context — which fails closed but leaves the demo unreachable
/// with no error anywhere.
/// </param>
/// <seealso cref="ITenantAccessor"/>
public record TenantContext(Guid TenantId, string Slug, string DisplayName, bool IsActive, bool IsDemo);
