using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Models.OAuth;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Unit tests for the <see cref="OAuthController"/> Dynamic Client Registration endpoint,
/// focused on the redirect_uris requirement. Device-flow clients (RFC 8628, e.g. the
/// Windows widget) register without any redirect_uris, so registration must succeed when
/// none are supplied.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "OAuth")]
public class OAuthControllerRegisterTests
{
    private readonly Mock<IOAuthClientService> _clientService = new();
    private readonly RedirectUriValidator _redirectUriValidator = new();

    private OAuthController CreateController()
    {
        return new OAuthController(
            _clientService.Object,
            Mock.Of<IOAuthGrantService>(),
            Mock.Of<IOAuthTokenService>(),
            Mock.Of<IOAuthDeviceCodeService>(),
            Mock.Of<ISubjectService>(),
            Mock.Of<IJwtService>(),
            Mock.Of<IOAuthTokenRevocationCache>(),
            NullLogger<OAuthController>.Instance
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
    }

    [Fact]
    public async Task Register_WithNoRedirectUris_SucceedsForDeviceFlowClient()
    {
        _clientService
            .Setup(s => s.RegisterClientAsync(
                "com.nocturne.widget.windows",
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.Is<IReadOnlyList<string>>(u => u.Count == 0),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthClientInfo
            {
                ClientId = "generated-client-id",
                SoftwareId = "com.nocturne.widget.windows",
                IsKnown = true,
            });

        var controller = CreateController();
        var request = new ClientRegistrationRequest
        {
            SoftwareId = "com.nocturne.widget.windows",
            ClientName = "Nocturne Windows Widget",
            Scope = "glucose.read",
            RedirectUris = [],
        };

        var result = await controller.Register(request, _redirectUriValidator, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<ClientRegistrationResponse>().Subject;
        body.ClientId.Should().Be("generated-client-id");
        _clientService.VerifyAll();
    }

    [Fact]
    public async Task Register_WithInvalidRedirectUri_ReturnsBadRequest()
    {
        var controller = CreateController();
        var request = new ClientRegistrationRequest
        {
            SoftwareId = "com.example.app",
            Scope = "glucose.read",
            // Non-loopback http is not a valid native redirect URI (RFC 8252).
            RedirectUris = ["http://example.com/callback"],
        };

        var result = await controller.Register(request, _redirectUriValidator, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeOfType<OAuthError>()
            .Which.Error.Should().Be("invalid_redirect_uri");
        _clientService.Verify(
            s => s.RegisterClientAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Register_WithValidCustomSchemeRedirectUri_Succeeds()
    {
        _clientService
            .Setup(s => s.RegisterClientAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.Is<IReadOnlyList<string>>(u => u.Count == 1),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthClientInfo { ClientId = "generated-client-id" });

        var controller = CreateController();
        var request = new ClientRegistrationRequest
        {
            SoftwareId = "com.nocturne.widget.windows",
            Scope = "glucose.read",
            RedirectUris = ["com.nocturne.widget.windows://oauth/callback"],
        };

        var result = await controller.Register(request, _redirectUriValidator, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }
}
