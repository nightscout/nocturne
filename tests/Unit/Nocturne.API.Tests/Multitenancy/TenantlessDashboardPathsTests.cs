using FluentAssertions;
using Nocturne.API.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// The tenantless dashboard host (apex, or a reserved dashboard slug) resolves no tenant, so every
/// request it makes 404s unless the path is tenantless-allowed. These pin the paths that host
/// actually calls, and the tenant-scoped ones that must keep 404ing there.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TenantlessDashboardPathsTests
{
    [Theory]
    // Called on every page load; without it the dashboard 404s before rendering.
    [InlineData("/api/auth/oidc/session")]
    // Sign-out from the dashboard.
    [InlineData("/api/auth/oidc/logout")]
    // Drives the dashboard tiles.
    [InlineData("/api/v4/me/tenants/overview")]
    // Drives the navigation and tenant switcher.
    [InlineData("/api/v4/me/tenants")]
    // Resolves to an empty scope set tenantlessly, which is the correct answer.
    [InlineData("/api/v4/me/permissions")]
    public void The_tenantless_dashboard_paths_are_allowed(string path)
    {
        TenantResolutionMiddleware.IsTenantlessAllowed(path).Should().BeTrue();
    }

    [Theory]
    // Tenant-scoped reads must not become reachable without a tenant.
    [InlineData("/api/v4/entries")]
    [InlineData("/api/v4/treatments")]
    [InlineData("/api/v4/chart-data/dashboard")]
    [InlineData("/api/v4/me/tenants/overview/extra")]
    [InlineData("/api/v4/me/permissions/grant")]
    public void Tenant_scoped_paths_stay_gated(string path)
    {
        TenantResolutionMiddleware.IsTenantlessAllowed(path).Should().BeFalse();
    }

    [Fact]
    public void The_subject_tenant_list_is_matched_exactly_not_as_a_prefix()
    {
        // "/api/v4/me/tenants" is an exact-match entry: adding it as a prefix would expose every
        // per-tenant sub-route under it (member management, settings) on a host with no tenant.
        TenantResolutionMiddleware.IsTenantlessAllowed("/api/v4/me/tenants").Should().BeTrue();
        TenantResolutionMiddleware.IsTenantlessAllowed("/api/v4/me/tenants/anything-else")
            .Should().BeFalse();
    }
}
