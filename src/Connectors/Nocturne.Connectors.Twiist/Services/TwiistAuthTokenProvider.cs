using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Twiist.Configurations;
using Nocturne.Connectors.Twiist.Models;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.Twiist.Services;

/// <summary>
/// Token provider for Twiist Insight via AWS Cognito.
/// Handles USER_PASSWORD_AUTH login and REFRESH_TOKEN_AUTH refresh.
/// </summary>
public class TwiistAuthTokenProvider(
    HttpClient httpClient,
    IConnectorTokenCache tokenCache,
    IConnectorServerResolver<TwiistConnectorConfiguration> serverResolver,
    ITenantAccessor tenantAccessor,
    ILogger<TwiistAuthTokenProvider> logger,
    IRetryDelayStrategy retryDelayStrategy)
    : AuthTokenProviderBase<TwiistConnectorConfiguration>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
{
    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    /// <summary>
    /// Cognito access tokens typically expire in 1 hour. Refresh 5 minutes early.
    /// </summary>
    protected override int TokenLifetimeBufferMinutes => 5;

    protected override string ConnectorName => "Twiist";

    protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        TwiistConnectorConfiguration config, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;

        // Read refresh token from previously cached session metadata
        var cached = await _tokenCache.GetAsync(ConnectorName, _tenantAccessor.TenantId);
        var refreshToken = cached?.Metadata?.GetValueOrDefault("RefreshToken");

        var accessToken = await ExecuteWithRetryAsync(
            async attempt =>
            {
                _logger.LogInformation(
                    "Authenticating with Twiist Cognito for account: {Username} (attempt {Attempt}/{MaxRetries})",
                    config.Username,
                    attempt + 1,
                    maxRetries);

                // Try refresh token first if we have one
                if (!string.IsNullOrEmpty(refreshToken))
                {
                    var refreshResult = await TryRefreshTokenAsync(refreshToken, cancellationToken);
                    if (refreshResult != null)
                        return (refreshResult, false);

                    _logger.LogInformation("Refresh token expired, falling back to password auth");
                    refreshToken = null;
                }

                // Fall back to password auth
                var (loginAccessToken, loginRefreshToken) = await LoginWithPasswordAsync(config, cancellationToken);
                if (loginAccessToken == null)
                    return (null, true);

                if (!string.IsNullOrEmpty(loginRefreshToken))
                    refreshToken = loginRefreshToken;

                return (loginAccessToken, false);
            },
            _retryDelayStrategy,
            maxRetries,
            "Twiist Cognito authentication",
            cancellationToken);

        if (string.IsNullOrEmpty(accessToken))
            return (null, DateTime.MinValue, null);

        // Cognito tokens expire in ~1 hour
        var expiresAt = DateTime.UtcNow.AddHours(1);
        _logger.LogInformation(
            "Twiist Cognito authentication successful, token expires at {ExpiresAt}",
            expiresAt);

        var metadata = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(refreshToken))
            metadata["RefreshToken"] = refreshToken;

        return (accessToken, expiresAt, metadata.Count > 0 ? metadata : null);
    }

    private async Task<(string? AccessToken, string? RefreshToken)> LoginWithPasswordAsync(
        TwiistConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            AuthFlow = "USER_PASSWORD_AUTH",
            AuthParameters = new
            {
                USERNAME = config.Username,
                PASSWORD = config.Password
            },
            ClientId = TwiistConstants.Cognito.ClientId
        });

        var result = await PostCognitoAsync(body, cancellationToken);
        if (result?.AuthenticationResult == null)
            return (null, null);

        return (result.AuthenticationResult.AccessToken, result.AuthenticationResult.RefreshToken);
    }

    private async Task<string?> TryRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            AuthFlow = "REFRESH_TOKEN_AUTH",
            AuthParameters = new
            {
                REFRESH_TOKEN = refreshToken
            },
            ClientId = TwiistConstants.Cognito.ClientId
        });

        try
        {
            var result = await PostCognitoAsync(body, cancellationToken);
            return result?.AuthenticationResult?.AccessToken;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<CognitoAuthResponse?> PostCognitoAsync(
        string jsonBody, CancellationToken cancellationToken)
    {
        var url = $"{TwiistConstants.Cognito.BaseUrl}{TwiistConstants.Cognito.PoolId}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, TwiistConstants.Cognito.ContentType);
        request.Headers.Add("X-Amz-Target", TwiistConstants.Cognito.AmzTarget);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Cognito authentication failed with HTTP {StatusCode}: {Error}",
                (int)response.StatusCode,
                errorBody);

            if ((int)response.StatusCode == 401 || (int)response.StatusCode == 400)
                throw new HttpRequestException(
                    $"Cognito auth failed: {response.StatusCode}",
                    null,
                    response.StatusCode);

            return null;
        }

        return await JsonSerializer.DeserializeAsync<CognitoAuthResponse>(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }
}
