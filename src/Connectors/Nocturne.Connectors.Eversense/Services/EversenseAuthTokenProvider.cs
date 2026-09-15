using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Eversense.Configurations;
using Nocturne.Connectors.Eversense.Models;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.Eversense.Services;

/// <summary>
///     Token provider for Eversense Now authentication.
///     Uses OAuth2 resource owner password grant against the Eversense IAM endpoint.
/// </summary>
public class EversenseAuthTokenProvider(
    HttpClient httpClient,
    IConnectorTokenCache tokenCache,
    IConnectorServerResolver<EversenseConnectorConfiguration> serverResolver,
    ITenantAccessor tenantAccessor,
    ILogger<EversenseAuthTokenProvider> logger,
    IRetryDelayStrategy retryDelayStrategy)
    : AuthTokenProviderBase<EversenseConnectorConfiguration>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
{
    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    protected override int TokenLifetimeBufferMinutes => 5;

    protected override string ConnectorName => "Eversense";

    protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        EversenseConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var maxRetries = LoginAttempts(config);

        var token = await ExecuteWithRetryAsync(
            async attempt =>
            {
                _logger.LogInformation(
                    "Authenticating with Eversense Now for account: {Username} (attempt {Attempt}/{MaxRetries})",
                    config.Username,
                    attempt + 1,
                    maxRetries);

                return await RequestTokenAsync(config, cancellationToken);
            },
            _retryDelayStrategy,
            maxRetries,
            "Eversense authentication",
            cancellationToken
        );

        if (token == null)
            return (null, DateTime.MinValue, null);

        var expiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn);
        _logger.LogInformation(
            "Eversense Now authentication successful, token expires at {ExpiresAt}",
            expiresAt);

        return (token.AccessToken, expiresAt, null);
    }

    /// <summary>
    ///     Requests one token, or null plus whether the failure is worth another attempt. A 2xx
    ///     carrying no access token is Eversense's answer for these credentials, not a transient fault.
    /// </summary>
    private async Task<(EversenseTokenResponse? Token, bool ShouldRetry)> RequestTokenAsync(
        EversenseConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var authBaseUrl = config.Server.ToUpperInvariant() switch
        {
            "US" => EversenseConstants.Servers.UsAuth,
            _ => throw new ArgumentOutOfRangeException(nameof(config.Server), config.Server, "Unsupported Eversense server region")
        };

        var formContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = config.Username,
            ["password"] = config.Password,
            ["grant_type"] = "password",
            ["client_id"] = EversenseConstants.ClientId,
            ["client_secret"] = EversenseConstants.ClientSecret
        });

        var request = new HttpRequestMessage(HttpMethod.Post, $"{authBaseUrl}{EversenseConstants.Endpoints.Token}")
        {
            Content = formContent
        };

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
            return (null, await HandleErrorResponseAsync(response, "Eversense token request", cancellationToken));

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var tokenResponse = JsonSerializer.Deserialize<EversenseTokenResponse>(json);

        if (string.IsNullOrEmpty(tokenResponse?.AccessToken))
        {
            _logger.LogError("Eversense token response contained empty access token");
            return (null, false);
        }

        return (tokenResponse, false);
    }
}
