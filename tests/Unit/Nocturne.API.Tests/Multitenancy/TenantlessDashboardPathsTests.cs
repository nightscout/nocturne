using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
    // Driven by the client's expiry timer; a 404 here signs the shell out of a live session.
    [InlineData("/api/auth/oidc/refresh")]
    // Drives the dashboard tiles.
    [InlineData("/api/v4/me/tenants/overview")]
    // Drives the navigation and tenant switcher.
    [InlineData("/api/v4/me/tenants")]
    // Reports the caller's own global subject-role scopes; nothing tenant-derived.
    [InlineData("/api/v4/me/permissions")]
    // The login page's provider buttons — the only sign-in affordance on a tenantless host.
    [InlineData("/api/auth/oidc/providers")]
    // The caller's own units/format/theme, stored on the subject. The tiles render glucose in
    // the units held here, so a 404 is a wrong reading, not a cosmetic default.
    [InlineData("/api/v4/user/preferences")]
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
    // The tenant's own display configuration, which is a different endpoint to the subject's
    // preferences and must not be reachable off a tenant.
    [InlineData("/api/v4/ui-settings")]
    [InlineData("/api/v4/settings/glucose-processing")]
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

    [Fact]
    public void The_subject_tenant_list_admits_only_the_read()
    {
        // The same path takes a POST that creates a tenant (MyTenantsController.Create).
        // Self-service provisioning affects tenants rather than reading across them, and in the
        // hosted deployment it goes through billing, so it stays off a host with no tenant.
        TenantResolutionMiddleware.IsTenantlessAllowed("/api/v4/me/tenants", HttpMethods.Get)
            .Should().BeTrue();
        TenantResolutionMiddleware.IsTenantlessAllowed("/api/v4/me/tenants", HttpMethods.Post)
            .Should().BeFalse();
        TenantResolutionMiddleware.IsTenantlessAllowed("/api/v4/me/tenants", HttpMethods.Delete)
            .Should().BeFalse();
    }

    [Fact]
    public void The_session_refresh_admits_only_the_post()
    {
        // Refresh is subject-scoped — it validates the refresh token, reads global subject roles,
        // and mints an access token with no tenant pin — so it is safe without a tenant. POST is
        // the only verb the endpoint serves, and naming it keeps any future sibling verb gated.
        TenantResolutionMiddleware.IsTenantlessAllowed("/api/auth/oidc/refresh", HttpMethods.Post)
            .Should().BeTrue();
        TenantResolutionMiddleware.IsTenantlessAllowed("/api/auth/oidc/refresh", HttpMethods.Get)
            .Should().BeFalse();
        TenantResolutionMiddleware.IsTenantlessAllowed("/api/auth/oidc/refresh", HttpMethods.Delete)
            .Should().BeFalse();
    }

    [Fact]
    public void The_provider_list_admits_only_the_read()
    {
        // Listing the enabled providers exposes nothing tenant-scoped — the allow-listed
        // /api/auth/oidc/login already reads the same set on a tenantless host. GET is the only
        // verb the endpoint serves, and naming it keeps any future sibling verb gated.
        TenantResolutionMiddleware.IsTenantlessAllowed("/api/auth/oidc/providers", HttpMethods.Get)
            .Should().BeTrue();
        TenantResolutionMiddleware.IsTenantlessAllowed("/api/auth/oidc/providers", HttpMethods.Post)
            .Should().BeFalse();
        TenantResolutionMiddleware.IsTenantlessAllowed("/api/auth/oidc/providers", HttpMethods.Delete)
            .Should().BeFalse();
    }

    [Fact]
    public void An_unrestricted_path_admits_every_method()
    {
        // Most entries name no method, so narrowing one must not narrow the rest.
        TenantResolutionMiddleware.IsTenantlessAllowed("/api/auth/oidc/logout", HttpMethods.Post)
            .Should().BeTrue();
        TenantResolutionMiddleware.IsTenantlessAllowed("/api/v4/me/tenants/overview", HttpMethods.Get)
            .Should().BeTrue();
    }

    [Fact]
    public void Omitting_the_method_asks_about_the_path_under_any_method()
    {
        // The authorization coverage sweep enumerates the whole tenantless surface and has no
        // request to take a method from, so a method-restricted path must still be reported.
        TenantResolutionMiddleware.IsTenantlessAllowed("/api/v4/me/tenants").Should().BeTrue();
    }

    [Fact]
    public void An_empty_method_is_a_method_not_an_omission()
    {
        // Only an omitted method means "any". A real request always carries one, and treating
        // the empty string as a wildcard would admit a blank-verb request to a narrowed path.
        TenantResolutionMiddleware.IsTenantlessAllowed("/api/v4/me/tenants", "").Should().BeFalse();
    }
}
