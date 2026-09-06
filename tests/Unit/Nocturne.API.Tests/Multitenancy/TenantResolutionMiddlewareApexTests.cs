using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
    public async Task Apex_non_tenantless_path_with_no_tenants_returns_503_setup_required()
    {
        var (context, nextCalled) = await InvokeAsync(BaseDomain, "/api/v4/entries");

        // Fresh install (zero tenants), non-tenantless path: 503 so the frontend goes to /setup.
        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }
}
