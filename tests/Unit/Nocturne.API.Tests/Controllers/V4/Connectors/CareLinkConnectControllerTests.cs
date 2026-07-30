using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Connectors;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Connectors;

/// <summary>
/// Verifies the desktop link-code mint: the token must be pinned to the caller's tenant and
/// subject, carry only the connect scope, and the link code must point at the host the user
/// actually used (X-Forwarded-Host when proxied).
/// </summary>
public sealed class CareLinkConnectControllerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();

    private readonly Mock<IJwtService> _jwtService = new();

    private CareLinkConnectController Build(TenantContext? tenant, AuthContext? auth)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(a => a.Context).Returns(tenant);

        var controller = new CareLinkConnectController(
            Mock.Of<IConnectorConfigurationService>(),
            new MemoryCache(new MemoryCacheOptions()),
            tenantAccessor.Object,
            _jwtService.Object,
            NullLoggerFactory.Instance,
            NullLogger<CareLinkConnectController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("acme.nocturne.run");
        if (auth != null)
        {
            httpContext.Items["AuthContext"] = auth;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static TenantContext Tenant() => new(TenantId, "acme", "Acme", IsActive: true);

    private static AuthContext Auth() => new()
    {
        IsAuthenticated = true,
        SubjectId = SubjectId,
        SubjectName = "Acme User",
        Email = "user@example.com",
    };

    [Fact]
    public void DesktopToken_mints_a_tenant_pinned_connect_scoped_token()
    {
        SubjectInfo? subject = null;
        IEnumerable<string>? scopes = null;
        IEnumerable<string>? permissions = null;
        Guid? tenantId = null;
        TimeSpan? lifetime = null;

        _jwtService
            .Setup(j => j.GenerateAccessToken(
                It.IsAny<SubjectInfo>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<Guid?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<Guid?>()))
            .Callback((SubjectInfo s, IEnumerable<string> p, IEnumerable<string> _, IEnumerable<string> sc,
                string? _, bool _, Guid? t, TimeSpan? l, bool _, Guid? _) =>
            {
                subject = s;
                permissions = p;
                scopes = sc;
                tenantId = t;
                lifetime = l;
            })
            .Returns("the-token");

        var result = Build(Tenant(), Auth()).DesktopToken();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<CareLinkDesktopTokenResponse>().Subject;

        subject!.Id.Should().Be(SubjectId);
        permissions.Should().BeEmpty();
        scopes.Should().BeEquivalentTo(["connectors:carelink:connect"]);
        tenantId.Should().Be(TenantId);
        lifetime.Should().Be(TimeSpan.FromMinutes(10));

        response.ExpiresInSeconds.Should().Be(600);
        response.LinkCode.Should().StartWith("nocturne-connect://link?server=");
        response.LinkCode.Should().Contain(Uri.EscapeDataString("https://acme.nocturne.run"));
        response.LinkCode.Should().EndWith("&token=the-token");
    }

    [Fact]
    public void DesktopToken_prefers_the_forwarded_host_and_scheme()
    {
        _jwtService
            .Setup(j => j.GenerateAccessToken(
                It.IsAny<SubjectInfo>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Guid?>()))
            .Returns("t");

        var controller = Build(Tenant(), Auth());
        controller.HttpContext.Request.Host = new HostString("nocturne-api", 8080);
        controller.HttpContext.Request.Headers["X-Forwarded-Host"] = "acme.localhost:1612";
        controller.HttpContext.Request.Headers["X-Forwarded-Proto"] = "https";

        var result = controller.DesktopToken();

        var response = (CareLinkDesktopTokenResponse)((OkObjectResult)result.Result!).Value!;
        response.LinkCode.Should().Contain(Uri.EscapeDataString("https://acme.localhost:1612"));
    }

    [Fact]
    public void DesktopToken_rejects_an_unauthenticated_caller()
    {
        var result = Build(Tenant(), auth: null).DesktopToken();
        result.Result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public void DesktopToken_rejects_an_unresolved_tenant()
    {
        var result = Build(tenant: null, Auth()).DesktopToken();
        result.Result.Should().BeOfType<UnauthorizedResult>();
    }

    /// <summary>
    /// The connect flow stores credentials, but the connector also needs a username (a required
    /// setting) and the region those tokens belong to. The desktop companion has no settings form,
    /// so without this the tenant ends up with credentials and a connector that never syncs.
    /// </summary>
    [Fact]
    public void MergeSignedInAccount_AppliesAccountAndRegion_KeepingOtherSettings()
    {
        using var existing = JsonDocument.Parse(
            """{"enabled":true,"server":"US","syncIntervalMinutes":5,"username":""}""");

        using var merged = CareLinkConnectController.MergeSignedInAccount(
            existing, "EU", "carer@example.com", "AU");

        var root = merged.RootElement;
        root.GetProperty("server").GetString().Should().Be("EU",
            "the stored tokens belong to the region signed in to — a stale region sends every data "
            + "request to a host that rejects them");
        root.GetProperty("username").GetString().Should().Be("carer@example.com");
        root.GetProperty("countryCode").GetString().Should().Be("au");
        root.GetProperty("syncIntervalMinutes").GetInt32().Should().Be(5,
            "SaveConfigurationAsync replaces the whole document, so unrelated settings must survive");
        root.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void MergeSignedInAccount_WithNoStoredConfiguration_StillRecordsTheRegion()
    {
        using var merged = CareLinkConnectController.MergeSignedInAccount(null, "EU", null, null);

        merged.RootElement.GetProperty("server").GetString().Should().Be("EU");
        merged.RootElement.TryGetProperty("username", out _).Should().BeFalse(
            "a username the profile did not report must not be invented");
    }
}
