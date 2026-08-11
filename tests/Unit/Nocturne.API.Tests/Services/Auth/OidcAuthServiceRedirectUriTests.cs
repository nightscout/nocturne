using System.Web;
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
/// Tests that the OIDC <c>redirect_uri</c> sent to providers is derived from
/// <c>BASE_DOMAIN</c> — the platform's single public-origin setting. Historically a
/// separate <c>BaseUrl</c> key fed this and every self-hosted deployment that set only
/// <c>BASE_DOMAIN</c> silently sent <c>http://localhost:5000</c> to its provider.
/// </summary>
public class OidcAuthServiceRedirectUriTests
{
    private static readonly Guid ProviderId = Guid.NewGuid();

    private static OidcAuthService BuildService(BaseDomainOptions baseDomainOptions)
    {
        var providerService = new Mock<IOidcProviderService>();
        providerService
            .Setup(p => p.GetProviderByIdAsync(ProviderId))
            .ReturnsAsync(new OidcProvider
            {
                Id = ProviderId,
                Name = "Keycloak",
                IssuerUrl = "https://issuer.example",
                ClientId = "nocturne",
                IsEnabled = true,
            });
        providerService
            .Setup(p => p.GetDiscoveryDocumentAsync(ProviderId))
            .ReturnsAsync(new OidcDiscoveryDocument
            {
                Issuer = "https://issuer.example",
                AuthorizationEndpoint = "https://issuer.example/authorize",
                TokenEndpoint = "https://issuer.example/token",
            });

        return new OidcAuthService(
            providerService.Object,
            new Mock<ISubjectService>().Object,
            new Mock<ISessionService>().Object,
            new Mock<IJwtService>().Object,
            new Mock<IRefreshTokenService>().Object,
            new Mock<IHttpClientFactory>().Object,
            new Mock<ITenantMemberService>().Object,
            new Mock<IMemberInviteService>().Object,
            new EphemeralDataProtectionProvider(),
            Options.Create(new OidcOptions()),
            Options.Create(baseDomainOptions),
            NullLogger<OidcAuthService>.Instance);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateAuthorizationUrl_DerivesRedirectUriFromBaseDomain()
    {
        var service = BuildService(new BaseDomainOptions { BaseDomain = "cgm.example.com" });

        var request = await service.GenerateAuthorizationUrlAsync(ProviderId);

        var query = HttpUtility.ParseQueryString(new Uri(request.AuthorizationUrl).Query);
        query["redirect_uri"].Should().Be("https://cgm.example.com/api/auth/oidc/callback");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateAuthorizationUrl_WithoutBaseDomain_FailsInsteadOfSendingLocalhost()
    {
        var service = BuildService(new BaseDomainOptions { BaseDomain = "" });

        var act = () => service.GenerateAuthorizationUrlAsync(ProviderId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*BASE_DOMAIN*");
    }
}
