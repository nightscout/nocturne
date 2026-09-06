using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// Verifies the on-demand TLS authorization endpoint that Caddy's
/// <c>on_demand_tls.ask</c> calls before issuing a certificate: only the apex
/// domain, active tenant subdomains, and share hosts whose token is live.
/// </summary>
public sealed class TlsAuthorizationControllerTests
{
    private const string BaseDomain = "nocturne.run";

    private static TlsAuthorizationController Build(params TenantDto[] tenants) =>
        Build(shares: null, tenants);

    private static TlsAuthorizationController Build(
        IReadOnlyDictionary<string, TenantContext>? shares,
        params TenantDto[] tenants)
    {
        var tenantService = new Mock<ITenantService>();
        tenantService
            .Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenants.ToList());

        var shareTokens = new Mock<IShareTokenResolver>();
        shareTokens
            .Setup(s => s.ResolveWithoutRecordingAccessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string token, CancellationToken _) =>
            {
                if (shares is not null && shares.TryGetValue(token, out var tenant))
                    return tenant;
                return null;
            });

        return new TlsAuthorizationController(
            tenantService.Object,
            shareTokens.Object,
            Options.Create(new BaseDomainOptions { BaseDomain = BaseDomain }));
    }

    private static TenantContext ShareTenant(bool isActive) =>
        new(Guid.CreateVersion7(), "acme", "Acme", isActive, IsDemo: false);

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

    // ── Public share hosts, one label deeper than a tenant ───────────────────

    [Fact]
    public async Task Authorizes_a_share_host_whose_token_is_live()
    {
        // Without this the host reaches the tenant branch as the slug "tok3n.share",
        // which no slug can equal, and Caddy never issues a certificate for a share link.
        var controller = Build(
            shares: new Dictionary<string, TenantContext> { ["tok3n"] = ShareTenant(isActive: true) });

        var result = await controller.Authorize("tok3n.share.nocturne.run", default);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Rejects_a_share_host_whose_token_resolves_to_nothing()
    {
        // Accepting any well-formed share host would let a stranger with DNS pointed here
        // drive unbounded issuance, so the token has to resolve.
        var controller = Build(shares: new Dictionary<string, TenantContext>());

        var result = await controller.Authorize("madeup.share.nocturne.run", default);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Rejects_a_share_host_of_an_inactive_tenant()
    {
        var controller = Build(
            shares: new Dictionary<string, TenantContext> { ["tok3n"] = ShareTenant(isActive: false) });

        var result = await controller.Authorize("tok3n.share.nocturne.run", default);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Does_not_treat_the_bare_share_label_as_a_share_host()
    {
        // "share.nocturne.run" carries no token, so it takes the tenant branch rather than the
        // share one. It can never authorize: "share" is in TenantService.ReservedSlugs, so no
        // tenant holds that slug and nothing matches.
        var controller = Build(
            shares: new Dictionary<string, TenantContext> { ["tok3n"] = ShareTenant(isActive: true) },
            Tenant("acme", isActive: true));

        var result = await controller.Authorize("share.nocturne.run", default);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Rejects_a_host_nested_below_a_share_host()
    {
        var controller = Build(
            shares: new Dictionary<string, TenantContext> { ["tok3n"] = ShareTenant(isActive: true) });

        var result = await controller.Authorize("deeper.tok3n.share.nocturne.run", default);

        result.Should().BeOfType<NotFoundResult>();
    }
}
