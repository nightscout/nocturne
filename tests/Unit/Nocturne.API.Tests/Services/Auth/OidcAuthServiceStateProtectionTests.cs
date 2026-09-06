using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Helpers;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// The callback trusts what the state carries: <c>Intent</c> selects the flow and
/// <c>SubjectId</c> names the account an external identity is bound to. The state cookie is
/// only a CSRF defence — a caller acting as its own HTTP client supplies both halves of the
/// double-submit — so integrity has to come from the state itself.
/// </summary>
public class OidcAuthServiceStateProtectionTests
{
    private readonly Mock<IOidcProviderService> _providerService = new();
    private readonly OidcAuthService _service;

    private static readonly Guid ProviderId = Guid.NewGuid();

    public OidcAuthServiceStateProtectionTests()
    {
        _service = new OidcAuthService(
            _providerService.Object,
            new Mock<ISubjectService>().Object,
            new Mock<ISessionService>().Object,
            new Mock<IJwtService>().Object,
            new Mock<IRefreshTokenService>().Object,
            new Mock<IHttpClientFactory>().Object,
            new Mock<ITenantMemberService>().Object,
            new Mock<IMemberInviteService>().Object,
            new EphemeralDataProtectionProvider(),
            Options.Create(new OidcOptions()),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example.com" }),
            NullLogger<OidcAuthService>.Instance);

        _providerService
            .Setup(p => p.GetProviderByIdAsync(ProviderId))
            .ReturnsAsync(new OidcProvider
            {
                Id = ProviderId,
                Name = "Keycloak",
                IssuerUrl = "https://issuer.example",
                ClientId = "nocturne",
                IsEnabled = true,
            });

        _providerService
            .Setup(p => p.GetDiscoveryDocumentAsync(ProviderId))
            .ReturnsAsync(new OidcDiscoveryDocument
            {
                Issuer = "https://issuer.example",
                AuthorizationEndpoint = "https://issuer.example/authorize",
                TokenEndpoint = "https://issuer.example/token",
            });
    }

    /// <summary>
    /// Builds the state an attacker can construct unaided: plain base64url JSON, naming any
    /// subject and tenant it likes. This is exactly what the pre-fix <c>EncodeState</c> produced.
    /// </summary>
    /// <remarks>
    /// The slug is populated so <see cref="TryReadTenantSlug_ForAStateThisInstanceDidNotIssue_ReturnsNull"/>
    /// has something to find if the protector is ever bypassed. Without it that test passes
    /// against a plain decoder too, because there is no slug in the payload either way.
    /// </remarks>
    private static string ForgeState(string intent, Guid subjectId) =>
        Base64Url.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            ProviderId,
            ReturnUrl = "/",
            Nonce = (string?)null,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Intent = intent,
            SubjectId = subjectId,
            TenantSlug = "forged-tenant",
        })));

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleSetupCallback_WithForgedState_IsRejected()
    {
        var victimSubjectId = Guid.NewGuid();
        var forged = ForgeState("setup", victimSubjectId);

        // The attacker controls the cookie too, so the double-submit check passes.
        var result = await _service.HandleSetupCallbackAsync("any-code", forged, forged);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("invalid_state");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleCallback_WithForgedState_IsRejected()
    {
        var forged = ForgeState("login", Guid.NewGuid());

        var result = await _service.HandleCallbackAsync("any-code", forged, forged);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("invalid_state");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandleLinkCallback_WithForgedState_IsRejected()
    {
        var authenticatedSubjectId = Guid.NewGuid();
        var forged = ForgeState("link", authenticatedSubjectId);

        var result = await _service.HandleLinkCallbackAsync(
            "any-code", forged, forged, authenticatedSubjectId);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("invalid_state");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GeneratedSetupState_DoesNotExposeItsPayload()
    {
        var subjectId = Guid.NewGuid();

        var request = await _service.GenerateSetupAuthorizationUrlAsync(ProviderId, subjectId);

        // A reader who can decode the state can also rewrite it, so the subject must not be
        // legible in the first place.
        request.State.Should().NotContain(subjectId.ToString());
        DecodeAsPlainJson(request.State).Should().BeNull(
            "the state must not be readable as plain base64url JSON");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TryReadTenantSlug_RoundTripsTheSlugItWasIssuedWith()
    {
        // The seam OidcCallbackRedirectMiddleware depends on. It stubs TryReadTenantSlug, so
        // without this nothing asserts that a real generated state actually yields its slug —
        // which is how protecting the payload silently broke the apex-to-subdomain redirect.
        var request = await _service.GenerateAuthorizationUrlAsync(ProviderId, "/", tenantSlug: "erik");

        _service.TryReadTenantSlug(request.State).Should().Be("erik");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryReadTenantSlug_ForAStateThisInstanceDidNotIssue_ReturnsNull()
    {
        _service.TryReadTenantSlug(ForgeState("login", Guid.NewGuid())).Should().BeNull();
        _service.TryReadTenantSlug("not-a-state").Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StateFromAnotherKeyRing_IsRejected()
    {
        // Stands in for any state this instance did not issue. A well-formed state minted
        // elsewhere must not be honoured here, which is only true while the payload is
        // authenticated rather than merely encoded.
        var foreign = await BuildServiceWithOwnKeyRing()
            .GenerateSetupAuthorizationUrlAsync(ProviderId, Guid.NewGuid());

        var result = await _service.HandleSetupCallbackAsync(
            "any-code", foreign.State, foreign.State);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("invalid_state");
    }

    private OidcAuthService BuildServiceWithOwnKeyRing() =>
        new(
            _providerService.Object,
            new Mock<ISubjectService>().Object,
            new Mock<ISessionService>().Object,
            new Mock<IJwtService>().Object,
            new Mock<IRefreshTokenService>().Object,
            new Mock<IHttpClientFactory>().Object,
            new Mock<ITenantMemberService>().Object,
            new Mock<IMemberInviteService>().Object,
            new EphemeralDataProtectionProvider(),
            Options.Create(new OidcOptions()),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example.com" }),
            NullLogger<OidcAuthService>.Instance);

    private static JsonDocument? DecodeAsPlainJson(string state)
    {
        try
        {
            return JsonDocument.Parse(Encoding.UTF8.GetString(Base64Url.Decode(state)));
        }
        catch
        {
            return null;
        }
    }
}
