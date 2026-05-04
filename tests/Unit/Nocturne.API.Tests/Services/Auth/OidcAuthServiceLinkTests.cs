using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Focused tests for the OIDC link branching logic extracted to
/// <see cref="OidcAuthService.AttachVerifiedIdentityAsync"/>.
/// </summary>
public class OidcAuthServiceLinkTests
{
    private readonly Mock<ISubjectService> _subjectService = new();
    private readonly Mock<IOidcProviderService> _providerService = new();
    private readonly Mock<ISessionService> _sessionService = new();
    private readonly Mock<IJwtService> _jwtService = new();
    private readonly Mock<IRefreshTokenService> _refreshTokenService = new();
    private readonly Mock<IHttpClientFactory> _httpFactory = new();
    private readonly Mock<IConfiguration> _configuration = new();
    private readonly OidcAuthService _service;

    public OidcAuthServiceLinkTests()
    {
        var options = Options.Create(new OidcOptions());
        _service = new OidcAuthService(
            _providerService.Object,
            _subjectService.Object,
            _sessionService.Object,
            _jwtService.Object,
            _refreshTokenService.Object,
            _httpFactory.Object,
            options,
            _configuration.Object,
            NullLogger<OidcAuthService>.Instance);
    }

    private static OidcAuthService.OidcStateData LinkState(Guid subjectId, string returnUrl = "/settings/account")
        => new()
        {
            Intent = "link",
            SubjectId = subjectId,
            ReturnUrl = returnUrl,
            ProviderId = Guid.NewGuid(),
            Nonce = "n",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

    private static OidcProvider Provider(Guid? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            Name = "Keycloak",
            IssuerUrl = "https://issuer.example",
            ClientId = "nocturne",
            IsEnabled = true,
        };

    private static OidcAuthService.OidcIdTokenClaims Claims(string sub = "ext-1", string? email = "user@x")
        => new() { Sub = sub, Email = email };

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AttachVerifiedIdentityAsync_WithLinkIntent_CallsSubjectService_ReturnsSucceeded()
    {
        var subjectId = Guid.NewGuid();
        var provider = Provider();
        var newIdentityId = Guid.NewGuid();

        _subjectService
            .Setup(s => s.AttachOidcIdentityAsync(subjectId, provider.Id, "ext-1", provider.IssuerUrl, "user@x"))
            .ReturnsAsync((OidcLinkOutcome.Created, (Guid?)newIdentityId));

        var result = await _service.AttachVerifiedIdentityAsync(
            LinkState(subjectId, "/x"), provider, Claims(), subjectId);

        result.Success.Should().BeTrue();
        result.IdentityId.Should().Be(newIdentityId);
        result.ReturnUrl.Should().Be("/x");

        _subjectService.Verify(
            s => s.AttachOidcIdentityAsync(subjectId, provider.Id, "ext-1", provider.IssuerUrl, "user@x"),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AttachVerifiedIdentityAsync_WithLoginIntent_ReturnsInvalidIntent()
    {
        var subjectId = Guid.NewGuid();
        var state = LinkState(subjectId);
        state.Intent = "login";

        var result = await _service.AttachVerifiedIdentityAsync(state, Provider(), Claims(), subjectId);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("invalid_intent");
        _subjectService.Verify(
            s => s.AttachOidcIdentityAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AttachVerifiedIdentityAsync_WhenStateSubjectIdMismatchesCaller_ReturnsInvalidState()
    {
        var subjectA = Guid.NewGuid();
        var subjectB = Guid.NewGuid();

        var result = await _service.AttachVerifiedIdentityAsync(
            LinkState(subjectA), Provider(), Claims(), subjectB);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("invalid_state");
        _subjectService.Verify(
            s => s.AttachOidcIdentityAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AttachVerifiedIdentityAsync_WhenAlreadyLinkedToSelf_ReturnsSucceededIdempotent()
    {
        var subjectId = Guid.NewGuid();
        var existing = Guid.NewGuid();

        _subjectService
            .Setup(s => s.AttachOidcIdentityAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((OidcLinkOutcome.AlreadyLinkedToSelf, (Guid?)existing));

        var result = await _service.AttachVerifiedIdentityAsync(
            LinkState(subjectId), Provider(), Claims(), subjectId);

        result.Success.Should().BeTrue();
        result.IdentityId.Should().Be(existing);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AttachVerifiedIdentityAsync_WhenLinkedToOtherSubject_ReturnsIdentityAlreadyLinked()
    {
        var subjectId = Guid.NewGuid();

        _subjectService
            .Setup(s => s.AttachOidcIdentityAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((OidcLinkOutcome.AlreadyLinkedToOther, (Guid?)null));

        var result = await _service.AttachVerifiedIdentityAsync(
            LinkState(subjectId), Provider(), Claims(), subjectId);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("identity_already_linked");
    }
}
