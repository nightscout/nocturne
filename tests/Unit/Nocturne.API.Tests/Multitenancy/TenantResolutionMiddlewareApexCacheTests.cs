using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// The apex resolves through one cached answer whatever that answer is. Only a single-tenant
/// install has a tenant to cache, so caching nothing else left every apex request on every other
/// install re-reading the tenant table — twice on the arm that also asks whether the install is
/// fresh, each read on a context of its own.
/// </summary>
/// <remarks>
/// Contexts created stands in for round trips: every read here opens one, so an answer served
/// from cache creates none.
/// </remarks>
public sealed class TenantResolutionMiddlewareApexCacheTests : TenantResolutionMiddlewareTestBase
{
    private CountingContextFactory _contexts = null!;

    protected override void ConfigureServices(IServiceCollection services)
    {
        _contexts = new CountingContextFactory((IDbContextFactory<NocturneDbContext>)services
            .Last(d => d.ServiceType == typeof(IDbContextFactory<NocturneDbContext>))
            .ImplementationInstance!);
        services.AddSingleton<IDbContextFactory<NocturneDbContext>>(_contexts);
    }

    [Fact]
    public async Task Apex_on_a_multi_tenant_install_reads_the_tenant_table_once()
    {
        SeedTenant("alpha");
        SeedTenant("beta");

        var (first, _) = await InvokeAsync(BaseDomain, "/api/v4/entries");
        var afterFirst = _contexts.Created;
        var (second, served) = await InvokeAsync(BaseDomain, "/api/v4/entries");

        served.Should().BeFalse();
        first.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        second.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        _contexts.Created.Should().Be(afterFirst);
    }

    [Fact]
    public async Task Apex_status_and_an_apex_page_share_the_one_answer()
    {
        SeedTenant("alpha");
        SeedTenant("beta");

        await InvokeAsync(BaseDomain, "/api/v4/status");
        var afterStatus = _contexts.Created;
        var (page, served) = await InvokeAsync(BaseDomain, "/api/v4/entries");

        served.Should().BeFalse();
        page.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        _contexts.Created.Should().Be(afterStatus);
    }

    [Fact]
    public async Task Fresh_install_re_reads_rather_than_caching_that_it_has_no_tenants()
    {
        var (first, _) = await InvokeAsync(BaseDomain, "/api/v4/entries");
        var afterFirst = _contexts.Created;
        var (second, _) = await InvokeAsync(BaseDomain, "/api/v4/entries");

        first.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        second.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        _contexts.Created.Should().BeGreaterThan(afterFirst);
    }

    [Fact]
    public async Task A_tenant_created_while_a_fresh_install_answer_was_live_is_served_at_once()
    {
        var (fresh, _) = await InvokeAsync(BaseDomain, "/api/v4/entries");
        fresh.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);

        var tenantId = SeedTenant("alpha");
        var (served, nextCalled) = await InvokeAsync(BaseDomain, "/api/v4/entries");

        // Deliberately no eviction between the two.
        nextCalled.Should().BeTrue();
        Resolve<ITenantAccessor>(served).TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task Install_whose_only_tenant_is_inactive_keeps_404ing_from_the_cached_answer()
    {
        SeedTenant("paused", isActive: false);

        var (first, _) = await InvokeAsync(BaseDomain, "/api/v4/entries");
        var afterFirst = _contexts.Created;
        var (second, _) = await InvokeAsync(BaseDomain, "/api/v4/entries");

        // An inactive tenant is not servable but does exist, so this is a 404 and not the fresh
        // install's 503 — the distinction has to survive being answered from cache.
        first.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        second.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        _contexts.Created.Should().Be(afterFirst);
    }

    private sealed class CountingContextFactory(IDbContextFactory<NocturneDbContext> inner)
        : IDbContextFactory<NocturneDbContext>
    {
        public int Created { get; private set; }

        public NocturneDbContext CreateDbContext()
        {
            Created++;
            return inner.CreateDbContext();
        }
    }
}
