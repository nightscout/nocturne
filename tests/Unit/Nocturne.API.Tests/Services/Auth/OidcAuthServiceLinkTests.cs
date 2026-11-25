using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
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
    private readonly Mock<ITenantMemberService> _tenantMemberService = new();
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
            _tenantMemberService.Object,
            new Mock<IMemberInviteService>().Object,
            new Mock<ITenantCreationInviteService>().Object,
            new Mock<ITenantRepository>().Object,
            new SignInCompletion(new Mock<ITotpService>().Object, _tenantMemberService.Object,
                _sessionService.Object, _subjectService.Object, new Mock<IAuthAuditService>().Object),
            new EphemeralDataProtectionProvider(),
            options,
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example.com" }),
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

    private static OidcAuthService.OidcIdTokenClaims Claims(string sub = "ext-1", string? email = "user@x", bool? emailVerified = null)
        => new() { Sub = sub, Email = email, EmailVerified = emailVerified };

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AttachVerifiedIdentityAsync_WithLinkIntent_CallsSubjectService_ReturnsSucceeded()
    {
        var subjectId = Guid.NewGuid();
        var provider = Provider();
        var newIdentityId = Guid.NewGuid();

        _subjectService
            .Setup(s => s.AttachOidcIdentityAsync(subjectId, provider.Id, "ext-1", provider.IssuerUrl, "user@x", false))
            .ReturnsAsync((OidcLinkOutcome.Created, (Guid?)newIdentityId));

        var result = await _service.AttachVerifiedIdentityAsync(
            LinkState(subjectId, "/x"), provider, Claims(), subjectId);

        result.Success.Should().BeTrue();
        result.IdentityId.Should().Be(newIdentityId);
        result.ReturnUrl.Should().Be("/x");

        _subjectService.Verify(
            s => s.AttachOidcIdentityAsync(subjectId, provider.Id, "ext-1", provider.IssuerUrl, "user@x", false),
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
            s => s.AttachOidcIdentityAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()),
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
            s => s.AttachOidcIdentityAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()),
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
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
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
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync((OidcLinkOutcome.AlreadyLinkedToOther, (Guid?)null));

        var result = await _service.AttachVerifiedIdentityAsync(
            LinkState(subjectId), Provider(), Claims(), subjectId);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("identity_already_linked");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public async Task AttachVerifiedIdentityAsync_PassesOnlyAVerifiedAssertionAsVerified(bool? claimed, bool passed)
    {
        var subjectId = Guid.NewGuid();
        var provider = Provider();
        _subjectService
            .Setup(s => s.AttachOidcIdentityAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync((OidcLinkOutcome.Created, (Guid?)Guid.NewGuid()));

        await _service.AttachVerifiedIdentityAsync(
            LinkState(subjectId), provider, Claims(emailVerified: claimed), subjectId);

        _subjectService.Verify(
            s => s.AttachOidcIdentityAsync(subjectId, provider.Id, "ext-1", provider.IssuerUrl, "user@x", passed),
            Times.Once);
    }

    private void AttachCreates() =>
        _subjectService
            .Setup(s => s.AttachOidcIdentityAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync((OidcLinkOutcome.Created, (Guid?)Guid.NewGuid()));

    private void ShouldHaveAttachedNothing() =>
        _subjectService.Verify(
            s => s.AttachOidcIdentityAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Never);

    private static OidcAuthService.OidcStateData RecoveryState(Guid subjectId)
    {
        var state = LinkState(subjectId);
        state.Intent = OidcAuthService.RecoveryIntent;
        return state;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARecoveryState_AttachesToTheRecoverySessionsSubjectAndNamesTheProvider()
    {
        var subjectId = Guid.NewGuid();
        var provider = Provider();
        AttachCreates();

        var result = await _service.AttachVerifiedIdentityAsync(
            RecoveryState(subjectId), provider, Claims(emailVerified: true), subjectId, OidcAuthService.RecoveryIntent);

        result.Success.Should().BeTrue();
        result.ProviderId.Should().Be(provider.Id);
        _subjectService.Verify(
            s => s.AttachOidcIdentityAsync(subjectId, provider.Id, "ext-1", provider.IssuerUrl, "user@x", true),
            Times.Once, "a verified provider address equal to the account's confirms it, as any link does");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARecoveryStateForSubjectA_DoesNotCompleteUnderARecoverySessionForSubjectB()
    {
        AttachCreates();

        var result = await _service.AttachVerifiedIdentityAsync(
            RecoveryState(Guid.NewGuid()), Provider(), Claims(), Guid.NewGuid(), OidcAuthService.RecoveryIntent);

        result.Error.Should().Be("invalid_state");
        ShouldHaveAttachedNothing();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARecoveryState_DoesNotCompleteAsASignedInLink()
    {
        var subjectId = Guid.NewGuid();
        AttachCreates();

        var result = await _service.AttachVerifiedIdentityAsync(RecoveryState(subjectId), Provider(), Claims(), subjectId);

        result.Error.Should().Be("invalid_intent");
        ShouldHaveAttachedNothing();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ALinkState_DoesNotCompleteUnderARecoverySession()
    {
        var subjectId = Guid.NewGuid();
        AttachCreates();

        var result = await _service.AttachVerifiedIdentityAsync(
            LinkState(subjectId), Provider(), Claims(), subjectId, OidcAuthService.RecoveryIntent);

        result.Error.Should().Be("invalid_intent");
        ShouldHaveAttachedNothing();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(OidcLinkOutcome.Created, true)]
    [InlineData(OidcLinkOutcome.AlreadyLinkedToSelf, false)]
    public async Task ALink_SaysWhetherItAddedTheIdentity(OidcLinkOutcome outcome, bool added)
    {
        var subjectId = Guid.NewGuid();
        _subjectService
            .Setup(s => s.AttachOidcIdentityAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync((outcome, (Guid?)Guid.NewGuid()));

        var result = await _service.AttachVerifiedIdentityAsync(
            RecoveryState(subjectId), Provider(), Claims(), subjectId, OidcAuthService.RecoveryIntent);

        result.Success.Should().BeTrue();
        result.Added.Should().Be(added);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARecoveryLink_ToAnIdentityLinkedToSomeoneElse_IsRefused()
    {
        var subjectId = Guid.NewGuid();
        _subjectService
            .Setup(s => s.AttachOidcIdentityAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync((OidcLinkOutcome.AlreadyLinkedToOther, (Guid?)null));

        var result = await _service.AttachVerifiedIdentityAsync(
            RecoveryState(subjectId), Provider(), Claims(), subjectId, OidcAuthService.RecoveryIntent);

        result.Error.Should().Be("identity_already_linked");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task OnlyARecoveryLinksState_ReadsAsARecoveryState()
    {
        _providerService.Setup(p => p.GetProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync(Provider());
        _providerService.Setup(p => p.GetDiscoveryDocumentAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new OidcDiscoveryDocument { AuthorizationEndpoint = "https://idp.example/auth", Issuer = "https://issuer.example" });

        var recovery = await _service.GenerateRecoveryLinkAuthorizationUrlAsync(Guid.NewGuid(), Guid.NewGuid());
        var link = await _service.GenerateLinkAuthorizationUrlAsync(Guid.NewGuid(), Guid.NewGuid());

        _service.IsRecoveryState(recovery.State).Should().BeTrue();
        _service.IsRecoveryState(link.State).Should().BeFalse();
        _service.IsRecoveryState("forged").Should().BeFalse();
    }
}
