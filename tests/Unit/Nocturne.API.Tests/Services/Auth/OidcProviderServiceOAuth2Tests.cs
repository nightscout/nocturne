using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Tests generic OAuth2 handling in <see cref="OidcProviderService"/>: an OAuth2 provider resolves
/// its endpoints from its configured <see cref="OAuth2ProviderSettings"/> (no network fetch) and uses
/// its own scopes rather than having <c>openid</c> forced onto them. Exercised through config-managed
/// mode so no database is required. A standards-compliant OIDC provider guards the existing
/// <c>openid</c>-scope behaviour.
/// </summary>
public class OidcProviderServiceOAuth2Tests
{
    private static OidcProviderService BuildService(params OidcProviderConfig[] providers)
    {
        var options = Options.Create(new OidcOptions { Providers = [.. providers] });
        return new OidcProviderService(
            dbContext: null!, // config-managed reads never touch the database
            new Mock<IHttpClientFactory>().Object,
            NullLogger<OidcProviderService>.Instance,
            new EphemeralDataProtectionProvider(),
            options);
    }

    private static OidcProviderConfig OAuth2Config() => new()
    {
        Name = "Acme",
        ProviderType = OidcProviderType.OAuth2,
        IssuerUrl = "https://acme.example.com",
        ClientId = "acme-client",
        Scopes = ["read:user", "user:email"],
        OAuth2 = new OAuth2ProviderSettings
        {
            AuthorizationEndpoint = "https://acme.example.com/oauth/authorize",
            TokenEndpoint = "https://acme.example.com/oauth/token",
            UserInfoEndpoint = "https://api.acme.example.com/user",
        },
    };

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetEnabledProviders_OAuth2_KeepsConfiguredScopesAndSettings()
    {
        var service = BuildService(OAuth2Config());

        var provider = (await service.GetEnabledProvidersAsync()).Single();

        provider.ProviderType.Should().Be(OidcProviderType.OAuth2);
        provider.Scopes.Should().Equal("read:user", "user:email");
        provider.Scopes.Should().NotContain("openid", "openid must not be forced onto an OAuth2 provider");
        provider.OAuth2.Should().NotBeNull();
        provider.OAuth2!.UserInfoEndpoint.Should().Be("https://api.acme.example.com/user");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetDiscoveryDocument_OAuth2_IsBuiltFromConfiguredEndpointsWithoutNetwork()
    {
        var service = BuildService(OAuth2Config());
        var provider = (await service.GetEnabledProvidersAsync()).Single();

        // No HttpClient is configured to return anything; a populated document proves no fetch occurs.
        var doc = await service.GetDiscoveryDocumentAsync(provider.Id);

        doc.Should().NotBeNull();
        doc!.AuthorizationEndpoint.Should().Be("https://acme.example.com/oauth/authorize");
        doc.TokenEndpoint.Should().Be("https://acme.example.com/oauth/token");
        doc.UserInfoEndpoint.Should().Be("https://api.acme.example.com/user");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetEnabledProviders_Oidc_StillForcesOpenIdScope()
    {
        var oidc = new OidcProviderConfig
        {
            Name = "Google",
            ProviderType = OidcProviderType.Oidc,
            IssuerUrl = "https://accounts.google.com",
            ClientId = "google-client",
            Scopes = ["email"],
        };
        var service = BuildService(oidc);

        var provider = (await service.GetEnabledProvidersAsync()).Single();

        provider.ProviderType.Should().Be(OidcProviderType.Oidc);
        provider.Scopes.Should().Contain("openid");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetEnabledProviders_Oidc_WithDefaultEmptyScopes_BackfillsOidcDefaults()
    {
        // OidcProviderConfig.Scopes defaults to empty so config binding does not pollute scopes;
        // an OIDC provider that configures none must still get the standard openid/profile/email.
        var oidc = new OidcProviderConfig
        {
            Name = "Keycloak",
            ProviderType = OidcProviderType.Oidc,
            IssuerUrl = "https://id.example.com",
            ClientId = "kc-client",
            Scopes = [],
        };
        var service = BuildService(oidc);

        var provider = (await service.GetEnabledProvidersAsync()).Single();

        provider.Scopes.Should().Equal("openid", "profile", "email");
    }
}
