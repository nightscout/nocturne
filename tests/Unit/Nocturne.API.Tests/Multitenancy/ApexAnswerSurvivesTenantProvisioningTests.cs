using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// The apex answer after a tenant is provisioned through the admin API, which is how the hosted
/// deployment's billing side creates one.
/// </summary>
/// <remarks>
/// Only <see cref="Provisioning_a_second_tenant_stops_the_apex_serving_the_first"/> pins the
/// eviction: it is the case with a cached answer to invalidate, and it is the one that fails if
/// the eviction is dropped. Its sibling pins the eviction and the uncached fresh-install answer
/// together — either alone still serves the new tenant — so it is a guard on the pair, not on
/// provisioning.
/// </remarks>
public sealed class ApexAnswerSurvivesTenantProvisioningTests : TenantResolutionMiddlewareTestBase
{
    private TenantService Provisioner() => new(
        Root.GetRequiredService<IDbContextFactory<NocturneDbContext>>(),
        Root.GetRequiredService<IMemoryCache>(),
        Options.Create(new OperatorConfiguration()),
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<ITenantRoleService>(),
        NullLogger<TenantService>.Instance);

    [Fact]
    public async Task Provisioning_a_second_tenant_stops_the_apex_serving_the_first()
    {
        var alpha = SeedTenant("alpha");
        var (single, _) = await InvokeAsync(BaseDomain, "/api/v4/entries");
        Resolve<ITenantAccessor>(single).TenantId.Should().Be(alpha);

        await Provisioner().ProvisionWithOwnerAsync(
            "beta", "Beta", "beta-owner", "beta-owner@example.com",
            credential: null, oidcIdentity: null);

        var (after, served) = await InvokeAsync(BaseDomain, "/api/v4/entries");

        // Two tenants and no subdomain is ambiguous. Left cached, the apex would keep handing
        // alpha's data to requests that no longer name a tenant.
        served.Should().BeFalse();
        after.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        Resolve<ITenantAccessor>(after).IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task Provisioning_the_first_tenant_makes_the_apex_serve_it()
    {
        var (fresh, _) = await InvokeAsync(BaseDomain, "/api/v4/entries");
        fresh.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);

        var provisioned = await Provisioner().ProvisionWithOwnerAsync(
            "alpha", "Alpha", "alpha-owner", "alpha-owner@example.com",
            credential: null, oidcIdentity: null);

        var (after, served) = await InvokeAsync(BaseDomain, "/api/v4/entries");

        served.Should().BeTrue();
        Resolve<ITenantAccessor>(after).TenantId.Should().Be(provisioned.TenantId);
    }
}
