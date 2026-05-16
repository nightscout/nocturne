namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Resolved tenant information for the current request. Set on <see cref="ITenantAccessor"/>
/// by the tenant-resolution middleware and consumed by the DbContext for RLS enforcement.
/// </summary>
/// <param name="TenantId">The unique identifier of the resolved tenant.</param>
/// <param name="Slug">The URL-safe slug used to identify the tenant in routes.</param>
/// <param name="DisplayName">The human-readable display name of the tenant.</param>
/// <param name="IsActive">Whether the tenant is currently active and accepting data.</param>
/// <seealso cref="ITenantAccessor"/>
public record TenantContext(Guid TenantId, string Slug, string DisplayName, bool IsActive, bool IsDemo = false);
