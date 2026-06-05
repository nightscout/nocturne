using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// Verifies the on-demand TLS authorization endpoint that Caddy's
/// <c>on_demand_tls.ask</c> calls before issuing a certificate: only the apex
/// domain and active tenant subdomains are authorized.
/// </summary>
public sealed class TlsAuthorizationControllerTests
{
    private const string BaseDomain = "nocturne.run";

    private static TlsAuthorizationController Build(params TenantDto[] tenants)
    {
        var tenantService = new Mock<ITenantService>();
        tenantService
            .Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenants.ToList());

        return new TlsAuthorizationController(
            tenantService.Object,
            Options.Create(new BaseDomainOptions { BaseDomain = BaseDomain }));
    }

    private static TenantDto Tenant(string slug, bool isActive) =>
        new(Guid.CreateVersion7(), slug, slug, isActive, DateTime.UtcNow);

    [Fact]
    public async Task Authorizes_the_apex_domain()
    {
        var controller = Build();
        var result = await controller.Authorize("nocturne.run", default);
        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Authorizes_an_active_tenant_subdomain()
    {
        var controller = Build(Tenant("acme", isActive: true));
        var result = await controller.Authorize("acme.nocturne.run", default);
        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Rejects_a_subdomain_with_no_matching_tenant()
    {
        var controller = Build(Tenant("acme", isActive: true));
        var result = await controller.Authorize("ghost.nocturne.run", default);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Rejects_an_inactive_tenant_subdomain()
    {
        var controller = Build(Tenant("acme", isActive: false));
        var result = await controller.Authorize("acme.nocturne.run", default);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Rejects_a_foreign_domain()
    {
        var controller = Build(Tenant("acme", isActive: true));
        var result = await controller.Authorize("evil.com", default);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Rejects_a_missing_domain(string? domain)
    {
        var controller = Build();
        var result = await controller.Authorize(domain, default);
        result.Should().BeOfType<NotFoundResult>();
    }
}
