using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Tidepool.Configurations;
using Nocturne.Connectors.Tidepool.Models;

namespace Nocturne.Connectors.Tidepool.Services;

/// <summary>
///     Token provider for Tidepool authentication.
///     Uses HTTP Basic Auth to login, extracts session token from response header.
/// </summary>
public class TidepoolAuthTokenProvider(
    IOptions<TidepoolConnectorConfiguration> config,
    HttpClient httpClient,
    ILogger<TidepoolAuthTokenProvider> logger,
    IRetryDelayStrategy retryDelayStrategy)
    : AuthTokenProviderBase<TidepoolConnectorConfiguration>(config.Value, httpClient, logger)
{
    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    private string? _userId;

    /// <summary>
    ///     The authenticated user ID, used for data fetching endpoints.
    ///     Set from auth response unless overridden in configuration.
    /// </summary>
    public string? UserId => !string.IsNullOrEmpty(_config.UserId) ? _config.UserId : _userId;

    /// <summary>
    ///     Tidepool sessions last ~24 hours. Refresh at 23 hours.
    /// </summary>
    protected override int TokenLifetimeBufferMinutes => 60;

    protected override async Task<(string? Token, DateTime ExpiresAt)> AcquireTokenAsync(
        CancellationToken cancellationToken)
    {
        const int maxRetries = 3;

        var sessionToken = await ExecuteWithRetryAsync(
            async attempt =>
            {
                _logger.LogInformation(
                    "Authenticating with Tidepool for account: {Username} (attempt {Attempt}/{MaxRetries})",
                    _config.Username,
                    attempt + 1,
                    maxRetries);

                var token = await LoginAsync(cancellationToken);
                if (string.IsNullOrEmpty(token))
                    return (null, true);

                return (token, false);
            },
            _retryDelayStrategy,
            maxRetries,
            "Tidepool authentication",
            cancellationToken
        );

        if (string.IsNullOrEmpty(sessionToken))
            return (null, DateTime.MinValue);

        var expiresAt = DateTime.UtcNow.AddHours(24);
        _logger.LogInformation(
            "Tidepool authentication successful for user {UserId}, session expires at {ExpiresAt}",
            UserId,
            expiresAt);

        return (sessionToken, expiresAt);
    }

    private async Task<string?> LoginAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login");

        // Tidepool uses HTTP Basic Authentication
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{_config.Username}:{_config.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsRetryableError())
            {
                _logger.LogWarning(
                    "Tidepool authentication failed with retryable error: {StatusCode} - {Error}",
                    response.StatusCode,
                    errorContent);
                return null;
            }

            _logger.LogError(
                "Tidepool authentication failed with non-retryable error: {StatusCode} - {Error}",
                response.StatusCode,
                errorContent);
            return null;
        }

        // Session token is in the response header
        if (!response.Headers.TryGetValues(TidepoolConstants.Headers.SessionToken, out var tokenValues))
        {
            _logger.LogError("Tidepool authentication response missing {Header} header",
                TidepoolConstants.Headers.SessionToken);
            return null;
        }

        var token = tokenValues.FirstOrDefault();
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogError("Tidepool authentication returned empty session token");
            return null;
        }

        // Extract user ID from response body
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var authResponse = JsonSerializer.Deserialize<TidepoolAuthResponse>(body, JsonDefaults.CaseInsensitive);

        if (authResponse != null && !string.IsNullOrEmpty(authResponse.Userid))
        {
            _userId = authResponse.Userid;
            _logger.LogDebug("Tidepool user ID resolved to {UserId}", _userId);
        }
        else
        {
            _logger.LogWarning("Tidepool authentication response did not contain a user ID");
        }

        return token;
    }
}
