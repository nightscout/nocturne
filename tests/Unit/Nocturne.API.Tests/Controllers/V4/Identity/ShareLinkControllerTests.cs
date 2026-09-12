using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Models.Responses;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// Verifies the share-link endpoints are gated on the <c>sharing.manage</c> permission. The GET
/// gate is security-load-bearing because the returned URL embeds the share token.
/// </summary>
public sealed class ShareLinkControllerTests
{
    private readonly Mock<IShareLinkService> _service = new();
    private readonly Mock<IAuthAuditService> _audit = new();

    private ShareLinkController BuildController(params string[] grantedScopes)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(t => t.TenantId).Returns(Guid.NewGuid());

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);

        return new ShareLinkController(_service.Object, tenantAccessor.Object, _audit.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Fact]
    public async Task GetShareLink_without_sharing_manage_is_forbidden_and_does_not_read_the_link()
    {
        var controller = BuildController(/* no scopes */);

        var result = await controller.GetShareLink(CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        _service.Verify(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RevealShareLink_without_sharing_manage_is_forbidden_and_does_not_read_the_link()
    {
        var controller = BuildController(/* no scopes */);

        var result = await controller.RevealShareLink(CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        _service.Verify(s => s.RevealAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RevealShareLink_records_who_was_handed_the_link()
    {
        _service.Setup(s => s.RevealAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShareLinkDto { Enabled = true, Url = "https://abc.share.nocturne.run" });
        var controller = BuildController(Scope.SharingManage);

        await controller.RevealShareLink(CancellationToken.None);

        _audit.Verify(a => a.LogAsync(
            AuthAuditEventType.ShareLinkRevealed, It.IsAny<Guid?>(), true,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Guid?>(), It.IsAny<AuthAuditActor>(), It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task RevealShareLink_records_nothing_when_there_was_no_link_to_hand_over()
    {
        _service.Setup(s => s.RevealAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShareLinkDto { Enabled = true, Url = null, CanReveal = false });
        var controller = BuildController(Scope.SharingManage);

        await controller.RevealShareLink(CancellationToken.None);

        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RotateShareLink_without_sharing_manage_is_forbidden()
    {
        var controller = BuildController(/* no scopes */);

        var result = await controller.RotateShareLink(CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        _service.Verify(s => s.RotateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisableShareLink_without_sharing_manage_is_forbidden()
    {
        var controller = BuildController(/* no scopes */);

        var result = await controller.DisableShareLink(CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        _service.Verify(s => s.DisableAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetShareLink_with_sharing_manage_returns_the_link_state()
    {
        _service.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShareLinkDto { Enabled = false });
        var controller = BuildController(Scope.SharingManage);

        var result = await controller.GetShareLink(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        _service.Verify(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetShareLinkScopes_without_sharing_manage_is_forbidden()
    {
        var controller = BuildController(/* no scopes */);

        var result = await controller.SetShareLinkScopes(
            new SetShareScopesRequest([Scope.GlucoseRead]), CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        _service.Verify(s => s.SetScopesAsync(
            It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetShareLinkScopes_with_sharing_manage_applies_the_scopes()
    {
        _service.Setup(s => s.SetScopesAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShareLinkDto { Enabled = true, Scopes = [Scope.GlucoseRead] });
        var controller = BuildController(Scope.SharingManage);

        var result = await controller.SetShareLinkScopes(
            new SetShareScopesRequest([Scope.GlucoseRead]), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        _service.Verify(s => s.SetScopesAsync(
            It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetShareLinkScopes_returns_bad_request_for_invalid_scopes()
    {
        _service.Setup(s => s.SetScopesAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Invalid public share scopes: bogus.read"));
        var controller = BuildController(Scope.SharingManage);

        var result = await controller.SetShareLinkScopes(
            new SetShareScopesRequest(["bogus.read"]), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
