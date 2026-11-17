using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Hubs;

/// <summary>
/// Base hub class that enforces tenant context validation on WebSocket connections.
///
/// SignalR hubs are mapped after the middleware pipeline, so TenantResolutionMiddleware
/// does not run for WebSocket message frames. However, the initial HTTP upgrade request
/// DOES go through middleware, so TenantContext is available via HttpContext.Items.
///
/// This base class:
/// 1. Validates tenant context on connection and rejects connections without a valid tenant
/// 2. Stores the TenantContext in HttpContext.Items for the lifetime of the connection
/// 3. Provides a helper for derived hubs to access the tenant context
/// </summary>
public abstract class TenantAwareHub : Hub
{
    /// <summary>
    /// Gets the tenant context for the current connection.
    /// Available after OnConnectedAsync has validated the connection.
    /// </summary>
    protected TenantContext? TenantContext =>
        Context.GetHttpContext()?.GetTenantContext();

    /// <summary>
    /// Creates a tenant-scoped SignalR group name using the pattern "{tenantId}:{groupName}".
    /// Uses the immutable TenantId (GUID) instead of the mutable Slug to prevent
    /// cross-tenant data leakage if a tenant's slug is changed.
    /// </summary>
    /// <param name="groupName">The base group name (e.g., "authorized", "admin")</param>
    /// <returns>A tenant-scoped group name</returns>
    /// <exception cref="HubException">Thrown when tenant context is not available</exception>
    protected string TenantGroup(string groupName)
    {
        var tenantId = TenantContext?.TenantId.ToString()
            ?? throw new HubException("Cannot create tenant group: no tenant context resolved.");
        return FormatTenantGroup(tenantId, groupName);
    }

    /// <summary>
    /// Static helper to format a tenant-scoped group name.
    /// Used by both hub methods and the broadcast service to ensure consistent naming.
    /// </summary>
    /// <param name="tenantId">The tenant identifier (GUID as string)</param>
    /// <param name="groupName">The base group name</param>
    /// <returns>A tenant-scoped group name in the format "{tenantId}:{groupName}"</returns>
    public static string FormatTenantGroup(string tenantId, string groupName)
        => $"{tenantId}:{groupName}";

    /// <summary>
    /// The tenant id a connection expects its host to resolve to. A client that reaches a tenant by
    /// slug sends it so that a slug deleted and created again cannot attach it to the new tenant.
    /// </summary>
    public const string ExpectedTenantHeader = "X-Expected-Tenant-Id";

    /// <summary>
    /// Validates that a tenant context exists from the HTTP upgrade handshake.
    /// If no tenant context is available, the tenant is inactive, or it is not the tenant named in
    /// <see cref="ExpectedTenantHeader"/>, the connection is rejected before any hub method runs.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        var tenantContext = httpContext?.GetTenantContext();

        if (tenantContext == null)
        {
            throw new HubException("Connection rejected: no tenant context resolved.");
        }

        if (!tenantContext.IsActive)
        {
            throw new HubException("Connection rejected: tenant is inactive.");
        }

        var expected = httpContext!.Request.Headers[ExpectedTenantHeader].ToString();
        if (expected.Length > 0
            && (!Guid.TryParse(expected, out var expectedId) || expectedId != tenantContext.TenantId))
        {
            throw new HubException("Connection rejected: the host resolves to another tenant.");
        }

        await base.OnConnectedAsync();
    }
}
