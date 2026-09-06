using FluentAssertions;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// <see cref="TenantResolutionMiddleware"/> selects the tenant from <c>Request.Host</c>, which the
/// forwarded-headers middleware has already resolved. An <c>X-Forwarded-Host</c> header still on the
/// request is one that middleware refused, and carries no weight here.
/// </summary>
public sealed class TenantResolutionMiddlewareHostSourceTests : TenantResolutionMiddlewareTestBase
{
    [Fact]
    public async Task Host_rewritten_by_the_forwarded_headers_middleware_selects_the_tenant()
    {
        var tenantId = SeedTenant("acme");

        var (context, _) = await InvokeAsync($"acme.{BaseDomain}:1612");

        Resolve<ITenantAccessor>(context).TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task Forwarded_host_header_left_in_place_does_not_select_the_tenant()
    {
        var acme = SeedTenant("acme");
        SeedTenant("victim");

        var (context, _) = await InvokeAsync(
            $"acme.{BaseDomain}",
            configure: request => request.Headers["X-Forwarded-Host"] = $"victim.{BaseDomain}");

        Resolve<ITenantAccessor>(context).TenantId.Should().Be(acme);
    }
}
