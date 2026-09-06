using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Nocturne.Connectors.MyFitnessPal.Models;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.MyFitnessPal.Services;

/// <summary>
///     Token provider for MyFitnessPal. Uses the mobile OAuth2 endpoint, preferring the refresh
///     token grant and falling back to the password grant.
/// </summary>
/// <remarks>
///     The rotated refresh token and the user id are returned as session metadata so the connector
///     service can persist them; the user id is required as the <c>mfp-user-id</c> header on every
///     subsequent GraphQL call.
/// </remarks>
public class MyFitnessPalAuthTokenProvider(
    HttpClient httpClient,
    IConnectorTokenCache tokenCache,
    IConnectorServerResolver<MyFitnessPalConnectorConfiguration> serverResolver,
    ITenantAccessor tenantAccessor,
    ILogger<MyFitnessPalAuthTokenProvider> logger,
    IRetryDelayStrategy retryDelayStrategy)
    : AuthTokenProviderBase<MyFitnessPalConnectorConfiguration>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
{
    public const string RefreshTokenMetadataKey = "RefreshToken";
    public const string UserIdMetadataKey = "UserId";

    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    protected override string ConnectorName => "MyFitnessPal";

    protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        MyFitnessPalConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var token = await ExecuteWithRetryAsync(
            async attempt => await RequestTokenAsync(config, attempt, cancellationToken),
            _retryDelayStrategy,
            config.MaxRetryAttempts,
            "MyFitnessPal authentication",
            cancellationToken
        );

        if (token == null)
            return (null, DateTime.MinValue, null);

        // A missing or nonsensical lifetime would otherwise expire the token before the cache's
        // own safety buffer, so nothing would ever be served from it.
        var lifetime = token.ExpiresIn > 0
            ? TimeSpan.FromSeconds(token.ExpiresIn)
            : MyFitnessPalConstants.DefaultTokenLifetime;
        var expiresAt = DateTime.UtcNow.Add(lifetime);

        var metadata = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(token.RefreshToken))
            metadata[RefreshTokenMetadataKey] = token.RefreshToken;

        // The token endpoint only returns the user id on a fresh login, so fall back to the
        // previously persisted value on a refresh.
        var userId = token.UserId ?? config.UserId;
        if (!string.IsNullOrEmpty(userId))
            metadata[UserIdMetadataKey] = userId;

        _logger.LogInformation(
            "MyFitnessPal authentication successful, token expires at {ExpiresAt}",
            expiresAt);

        return (token.AccessToken, expiresAt, metadata);
    }

    /// <summary>
    ///     Tries the refresh token grant first, then the password grant. A refresh token that has
    ///     expired or been revoked yields a non-retryable 4xx, so the password fallback runs in the
    ///     same attempt rather than burning the retry budget.
    /// </summary>
    private async Task<(MfpTokenResponse? Result, bool ShouldRetry)> RequestTokenAsync(
        MyFitnessPalConnectorConfiguration config, int attempt, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(config.RefreshToken))
        {
            _logger.LogInformation(
                "Refreshing MyFitnessPal token for account {Username} (attempt {Attempt})",
                config.Username,
                attempt + 1);

            var (refreshed, _) = await PostTokenRequestAsync(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = config.RefreshToken,
                    ["client_id"] = MyFitnessPalConstants.ClientId,
                },
                "MyFitnessPal token refresh",
                cancellationToken);

            if (refreshed != null)
                return (refreshed, false);

            _logger.LogWarning("MyFitnessPal token refresh failed, falling back to password grant");
        }

        if (string.IsNullOrWhiteSpace(config.Password))
        {
            _logger.LogError("MyFitnessPal refresh token is unusable and no password is configured");
            return (null, false);
        }

        _logger.LogInformation(
            "Authenticating with MyFitnessPal for account {Username} (attempt {Attempt})",
            config.Username,
            attempt + 1);

        return await PostTokenRequestAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = config.Username,
                ["password"] = config.Password,
                ["client_id"] = MyFitnessPalConstants.ClientId,
            },
            "MyFitnessPal password grant",
            cancellationToken);
    }

    private async Task<(MfpTokenResponse? Result, bool ShouldRetry)> PostTokenRequestAsync(
        Dictionary<string, string> body,
        string operationName,
        CancellationToken cancellationToken)
    {
        var url = $"{MyFitnessPalConstants.Servers.Auth}{MyFitnessPalConstants.Endpoints.Token}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation(
            MyFitnessPalConstants.Headers.ClientId, MyFitnessPalConstants.ClientId);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // A wrong password or revoked refresh token is non-retryable. Retrying it burns the
            // multi-minute backoff, overruns the per-tenant sync timeout, and hammers the login
            // endpoint every cycle.
            var shouldRetry = await HandleErrorResponseAsync(response, operationName, cancellationToken);
            return (null, shouldRetry);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var tokenResponse = JsonSerializer.Deserialize<MfpTokenResponse>(json);

        if (string.IsNullOrEmpty(tokenResponse?.AccessToken))
        {
            _logger.LogError("{OperationName} returned an empty access token", operationName);
            return (null, false);
        }

        return (tokenResponse, false);
    }
}
