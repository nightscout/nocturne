using System.Collections.Concurrent;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// An issuer's OpenID configuration with its signing keys, for verifying a token the provider
/// sends us directly rather than over the token endpoint.
/// </summary>
public interface IOidcConfigurationSource
{
    /// <summary>The configuration published under <paramref name="issuerUrl"/>, cached and refreshed.</summary>
    Task<OpenIdConnectConfiguration> GetAsync(string issuerUrl, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class OidcConfigurationSource(IHttpClientFactory httpClientFactory) : IOidcConfigurationSource
{
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<OpenIdConnectConfiguration> GetAsync(string issuerUrl, CancellationToken ct = default) =>
        _managers.GetOrAdd(issuerUrl.TrimEnd('/'), issuer =>
            new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{issuer}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(httpClientFactory.CreateClient("OidcProvider")) { RequireHttps = true })
            {
                AutomaticRefreshInterval = TimeSpan.FromHours(12),
            })
            .GetConfigurationAsync(ct);
}
