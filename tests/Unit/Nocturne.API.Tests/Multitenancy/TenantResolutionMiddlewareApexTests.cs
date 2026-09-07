using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// Apex (no-subdomain) resolution in <see cref="TenantResolutionMiddleware"/>. Single-tenant
/// installs are served at the base domain itself, so the apex must resolve the sole tenant for
/// tenant-scoped paths — including otherwise-tenantless ones like <c>GET /api/v4/status</c>.
/// Leaving status tenantless on the apex made it report "setup_required", which bounced a fully
/// configured single-tenant install to /setup. Multi-tenant and zero-tenant behaviour is
/// unchanged, and infrastructure/liveness paths stay tenant-agnostic.
/// </summary>
public sealed class TenantResolutionMiddlewareApexTests : TenantResolutionMiddlewareTestBase
{
    protected override string BaseDomain => "nocturne.theconen.de";

    [Fact]
    public async Task Apex_status_resolves_the_sole_tenant_in_single_tenant_mode()
    {
        var tenantId = SeedTenant("theconen");

        var (context, nextCalled) = await InvokeAsync(BaseDomain, "/api/v4/status");

        // /api/v4/status is tenantless-allowed, but on the apex the sole tenant is resolved
        // so StatusService reports that tenant instead of "setup_required".
        nextCalled.Should().BeTrue();
        var accessor = Resolve<ITenantAccessor>(context);
        accessor.IsResolved.Should().BeTrue();
        accessor.TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task Apex_health_probe_passes_through_without_resolving_a_tenant()
    {
        SeedTenant("theconen");

        var (context, nextCalled) = await InvokeAsync(BaseDomain, "/health");

        // Liveness/readiness probes must never depend on tenant state — no resolution, no DB touch.
        nextCalled.Should().BeTrue();
        Resolve<ITenantAccessor>(context).IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task Apex_status_with_multiple_tenants_stays_tenantless()
    {
        // The demo takes the lowest id so it orders first: a demo dropped after the two-row
        // limit rather than before it would leave one row here and serve alpha from the apex.
        SeedTenant("demo", isDemo: true, id: new Guid("00000000-0000-7000-8000-000000000001"));
        SeedTenant("alpha");
        SeedTenant("beta");

        var (context, nextCalled) = await InvokeAsync(BaseDomain, "/api/v4/status");

        // With more than one tenant there is no sole tenant; status stays tenantless and passes
        // through, so the apex status response is unchanged for multi-tenant deployments.
        nextCalled.Should().BeTrue();
        Resolve<ITenantAccessor>(context).IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task Apex_status_with_a_sole_tenant_names_it_so_the_web_serves_the_full_app()
    {
        SeedTenant("theconen");

        var (context, _) = await InvokeAsync(BaseDomain, "/api/v4/status");

        // The status response carries this slug, and it is the only thing that tells the web app
        // an apex serving a single-tenant install apart from one serving the dashboard. Getting
        // it wrong trims that install's sidebar and bounces it to a wildcard subdomain.
        Resolve<ITenantAccessor>(context).Context!.Slug.Should().Be("theconen");
    }

    [Fact]
    public async Task Apex_tenant_list_is_readable_but_not_creatable()
    {
        SeedTenant("alpha");
        SeedTenant("beta");

        var (_, read) = await InvokeAsync(BaseDomain, "/api/v4/me/tenants");
        var (createContext, created) = await InvokeAsync(BaseDomain, "/api/v4/me/tenants", HttpMethods.Post);

        // The list is keyed on the caller's subject and is what the dashboard navigates by. The
        // POST on the same path provisions a tenant, which is not a cross-tenant read and has no
        // business on a host that resolves none — so it falls through to the ordinary apex 404.
        read.Should().BeTrue();
        created.Should().BeFalse();
        createContext.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Apex_keeps_resolving_the_operators_tenant_once_a_demo_is_provisioned()
    {
        var tenantId = SeedTenant("theconen");
        SeedTenant("demo", isDemo: true);
        // One scope for both requests: the pin has to survive the second resolution, not just
        // be recomputed from a clean scope each time.
        using var scope = Root.CreateScope();

        var (pageContext, served) = await InvokeAsync(scope, BaseDomain, "/api/v4/entries");
        var (statusContext, _) = await InvokeAsync(scope, BaseDomain, "/api/v4/status");

        // Turning the demo on is a second active tenant, which would otherwise end single-tenant
        // mode: every apex page 404s and status stops naming the operator's tenant.
        served.Should().BeTrue();
        pageContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        Resolve<ITenantAccessor>(pageContext).TenantId.Should().Be(tenantId);
        Resolve<ITenantAccessor>(statusContext).TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task Apex_with_only_a_demo_tenant_answers_as_a_fresh_install()
    {
        SeedTenant("demo", isDemo: true);
        using var scope = Root.CreateScope();

        var (pageContext, served) = await InvokeAsync(scope, BaseDomain, "/api/v4/entries");
        await InvokeAsync(scope, BaseDomain, "/api/v4/status");

        // The demo is served on its own host, never adopted by the apex — so an instance holding
        // only a demo has no tenant to resolve, and answers 503 so the frontend goes to /setup
        // rather than the 404 that would strand an operator with nowhere to create one.
        served.Should().BeFalse();
        pageContext.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        Resolve<ITenantAccessor>(pageContext).IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task Apex_with_a_single_inactive_tenant_does_not_resolve_it()
    {
        SeedTenant("paused", isActive: false);

        var (context, served) = await InvokeAsync(BaseDomain, "/api/v4/entries");

        served.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        Resolve<ITenantAccessor>(context).IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task Apex_non_tenantless_path_with_no_tenants_returns_503_setup_required()
    {
        var (context, nextCalled) = await InvokeAsync(BaseDomain, "/api/v4/entries");

        // Fresh install (zero tenants), non-tenantless path: 503 so the frontend goes to /setup.
        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }
}
