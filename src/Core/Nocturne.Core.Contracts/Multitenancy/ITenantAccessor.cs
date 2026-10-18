namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Provides access to the current tenant context.
/// Scoped per-request. Used by DbContext for query filters and RLS.
/// </summary>
public interface ITenantAccessor
{
    /// <summary>
    /// The current tenant's ID. Returns Guid.Empty if no tenant is resolved.
    /// </summary>
    Guid TenantId { get; }

    bool IsResolved { get; }

    /// <summary>
    /// The full resolved tenant context. Null if not resolved.
    /// </summary>
    TenantContext? Context { get; }

    /// <summary>
    /// Sets the tenant context for the current scope.
    /// Called by TenantResolutionMiddleware and by background services.
    /// </summary>
    void SetTenant(TenantContext context);
}
