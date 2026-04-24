using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Eversense.Configurations;
using Nocturne.Connectors.Eversense.Models;

namespace Nocturne.Connectors.Eversense.Services;

/// <summary>
///     Token provider for Eversense Now authentication.
///     Uses OAuth2 resource owner password grant against the Eversense IAM endpoint.
/// </summary>
public class EversenseAuthTokenProvider(
    IOptions<EversenseConnectorConfiguration> config,
    HttpClient httpClient,
    ILogger<EversenseAuthTokenProvider> logger,
    IRetryDelayStrategy retryDelayStrategy)
    : AuthTokenProviderBase<EversenseConnectorConfiguration>(config.Value, httpClient, logger)
{
    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    protected override int TokenLifetimeBufferMinutes => 5;

    protected override async Task<(string? Token, DateTime ExpiresAt)> AcquireTokenAsync(
        CancellationToken cancellationToken)
    {
        const int maxRetries = 3;

        var token = await ExecuteWithRetryAsync(
            async attempt =>
            {
                _logger.LogInformation(
                    "Authenticating with Eversense Now for account: {Username} (attempt {Attempt}/{MaxRetries})",
                    _config.Username,
                    attempt + 1,
                    maxRetries);

                var result = await RequestTokenAsync(cancellationToken);
                return (result, result == null);
            },
            _retryDelayStrategy,
            maxRetries,
            "Eversense authentication",
            cancellationToken
        );

        if (token == null)
            return (null, DateTime.MinValue);

        var expiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn);
        _logger.LogInformation(
            "Eversense Now authentication successful, token expires at {ExpiresAt}",
            expiresAt);

        return (token.AccessToken, expiresAt);
    }

    private async Task<EversenseTokenResponse?> RequestTokenAsync(CancellationToken cancellationToken)
    {
        var authBaseUrl = _config.Server.ToUpperInvariant() switch
        {
            "US" => EversenseConstants.Servers.UsAuth,
            _ => throw new ArgumentOutOfRangeException(nameof(_config.Server), _config.Server, "Unsupported Eversense server region")
        };

        var formContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = _config.Username,
            ["password"] = _config.Password,
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
        {
            await HandleErrorResponseAsync(response, "Eversense token request", cancellationToken);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var tokenResponse = JsonSerializer.Deserialize<EversenseTokenResponse>(json);

        if (string.IsNullOrEmpty(tokenResponse?.AccessToken))
        {
            _logger.LogError("Eversense token response contained empty access token");
            return null;
        }

        return tokenResponse;
    }
}
