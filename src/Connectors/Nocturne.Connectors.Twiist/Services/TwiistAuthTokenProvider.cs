using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Twiist.Configurations;
using Nocturne.Connectors.Twiist.Models;

namespace Nocturne.Connectors.Twiist.Services;

/// <summary>
/// Token provider for Twiist Insight via AWS Cognito.
/// Handles USER_PASSWORD_AUTH login and REFRESH_TOKEN_AUTH refresh.
/// </summary>
public class TwiistAuthTokenProvider(
    IOptions<TwiistConnectorConfiguration> config,
    HttpClient httpClient,
    ILogger<TwiistAuthTokenProvider> logger,
    IRetryDelayStrategy retryDelayStrategy)
    : AuthTokenProviderBase<TwiistConnectorConfiguration>(config.Value, httpClient, logger)
{
    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    private string? _refreshToken;

    /// <summary>
    /// Cognito access tokens typically expire in 1 hour. Refresh 5 minutes early.
    /// </summary>
    protected override int TokenLifetimeBufferMinutes => 5;

    protected override async Task<(string? Token, DateTime ExpiresAt)> AcquireTokenAsync(
        CancellationToken cancellationToken)
    {
        const int maxRetries = 3;

        var accessToken = await ExecuteWithRetryAsync(
            async attempt =>
            {
                _logger.LogInformation(
                    "Authenticating with Twiist Cognito for account: {Username} (attempt {Attempt}/{MaxRetries})",
                    _config.Username,
                    attempt + 1,
                    maxRetries);

                // Try refresh token first if we have one
                if (!string.IsNullOrEmpty(_refreshToken))
                {
                    var refreshResult = await TryRefreshTokenAsync(cancellationToken);
                    if (refreshResult != null)
                        return (refreshResult, false);

                    _logger.LogInformation("Refresh token expired, falling back to password auth");
                    _refreshToken = null;
                }

                // Fall back to password auth
                var loginResult = await LoginWithPasswordAsync(cancellationToken);
                if (loginResult == null)
                    return (null, true);

                return (loginResult, false);
            },
            _retryDelayStrategy,
            maxRetries,
            "Twiist Cognito authentication",
            cancellationToken);

        if (string.IsNullOrEmpty(accessToken))
            return (null, DateTime.MinValue);

        // Cognito tokens expire in ~1 hour
        var expiresAt = DateTime.UtcNow.AddHours(1);
        _logger.LogInformation(
            "Twiist Cognito authentication successful, token expires at {ExpiresAt}",
            expiresAt);

        return (accessToken, expiresAt);
    }

    private async Task<string?> LoginWithPasswordAsync(CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            AuthFlow = "USER_PASSWORD_AUTH",
            AuthParameters = new
            {
                USERNAME = _config.Username,
                PASSWORD = _config.Password
            },
            ClientId = TwiistConstants.Cognito.ClientId
        });

        var result = await PostCognitoAsync(body, cancellationToken);
        if (result?.AuthenticationResult == null)
            return null;

        // Cache the refresh token for future use
        if (!string.IsNullOrEmpty(result.AuthenticationResult.RefreshToken))
            _refreshToken = result.AuthenticationResult.RefreshToken;

        return result.AuthenticationResult.AccessToken;
    }

    private async Task<string?> TryRefreshTokenAsync(CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            AuthFlow = "REFRESH_TOKEN_AUTH",
            AuthParameters = new
            {
                REFRESH_TOKEN = _refreshToken!
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
