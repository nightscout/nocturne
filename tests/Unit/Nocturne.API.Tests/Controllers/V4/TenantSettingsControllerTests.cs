using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// The documentation opt-in is a tenant-administration setting, so it is gated on the same
/// <c>tenant.settings</c> atom the rest of that surface uses — a member who can read the tenant
/// must not be able to publish its API reference.
/// </summary>
public class TenantSettingsControllerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    [Fact]
    public async Task SetPublicDocs_RefusesAMemberWithoutTenantSettings()
    {
        var (controller, tenants) = Build(TenantPermissions.IdentityRead);

        var result = await controller.SetPublicDocs(new SetPublicDocsRequest(true), CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        tenants.Verify(
            t => t.SetAllowPublicDocsAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SetPublicDocs_WritesTheTenantsOwnFlag()
    {
        var (controller, tenants) = Build(TenantPermissions.TenantSettings);
        tenants.Setup(t => t.SetAllowPublicDocsAsync(TenantId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantSettingsDto(true));

        var result = await controller.SetPublicDocs(new SetPublicDocsRequest(true), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeEquivalentTo(new TenantSettingsDto(true));
        tenants.Verify(t => t.SetAllowPublicDocsAsync(TenantId, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetPublicDocs_TurnsTheDocumentationSurfaceOffAgain()
    {
        var (controller, tenants) = Build(TenantPermissions.TenantSettings);
        tenants.Setup(t => t.SetAllowPublicDocsAsync(TenantId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantSettingsDto(false));

        await controller.SetPublicDocs(new SetPublicDocsRequest(false), CancellationToken.None);

        tenants.Verify(t => t.SetAllowPublicDocsAsync(TenantId, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetTenantSettings_RefusesAMemberWithoutTenantSettings()
    {
        var (controller, tenants) = Build(TenantPermissions.IdentityRead);

        var result = await controller.GetTenantSettings(CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        tenants.Verify(
            t => t.GetSettingsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetTenantSettings_ReadsTheResolvedTenant()
    {
        var (controller, tenants) = Build(TenantPermissions.TenantSettings);
        tenants.Setup(t => t.GetSettingsAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantSettingsDto(true));

        var result = await controller.GetTenantSettings(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeEquivalentTo(new TenantSettingsDto(true));
    }

    private static (TenantSettingsController Controller, Mock<ITenantService> Tenants) Build(
        params string[] grantedScopes)
    {
        var tenants = new Mock<ITenantService>(MockBehavior.Strict);

        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(a => a.TenantId).Returns(TenantId);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);

        var controller = new TenantSettingsController(tenants.Object, accessor.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        return (controller, tenants);
    }
}
