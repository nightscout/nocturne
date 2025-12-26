using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tidepool.Constants;

namespace Nocturne.Connectors.Tidepool.Services;

/// <summary>
/// Token provider for Tidepool authentication.
/// Returns a session token and user ID for API requests.
/// </summary>
public class TidepoolAuthTokenProvider : AuthTokenProviderBase<TidepoolConnectorConfiguration>
{
    private readonly IRetryDelayStrategy _retryDelayStrategy;

    /// <summary>
    /// Gets the user ID obtained during authentication.
    /// Required for Tidepool API data requests.
    /// </summary>
    public string? UserId { get; private set; }

    public TidepoolAuthTokenProvider(
        IOptions<TidepoolConnectorConfiguration> config,
        HttpClient httpClient,
        ILogger<TidepoolAuthTokenProvider> logger,
        IRetryDelayStrategy retryDelayStrategy)
        : base(config.Value, httpClient, logger)
    {
        _retryDelayStrategy = retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
    }

    /// <summary>
    /// Tidepool tokens typically last 24 hours.
    /// </summary>
    protected override int TokenLifetimeBufferMinutes => 60;

    protected override async Task<(string? Token, DateTime ExpiresAt)> AcquireTokenAsync(CancellationToken cancellationToken)
    {
        const int maxRetries = 3;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "Authenticating with Tidepool for account: {Username} (attempt {Attempt}/{MaxRetries})",
                    _config.TidepoolUsername,
                    attempt + 1,
                    maxRetries);

                // Tidepool uses Basic authentication for login
                var authString = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{_config.TidepoolUsername}:{_config.TidepoolPassword}"));

                var request = new HttpRequestMessage(HttpMethod.Post, TidepoolConstants.Endpoints.Login);
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authString);

                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

                    if (response.IsRetryableError())
                    {
                        _logger.LogWarning(
                            "Tidepool authentication failed with retryable error on attempt {Attempt}: {StatusCode} - {Error}",
                            attempt + 1,
                            response.StatusCode,
                            errorContent);

                        if (attempt < maxRetries - 1)
                        {
                            await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
                            continue;
                        }
                    }
                    else
                    {
                        _logger.LogError(
                            "Tidepool authentication failed with non-retryable error: {StatusCode} - {Error}",
                            response.StatusCode,
                            errorContent);
                    }
                    return (null, DateTime.MinValue);
                }

                // Extract session token from response header
                string? sessionToken = null;
                if (response.Headers.TryGetValues(TidepoolConstants.Headers.SessionToken, out var tokenValues))
                {
                    sessionToken = tokenValues.FirstOrDefault();
                }

                if (string.IsNullOrEmpty(sessionToken))
                {
                    _logger.LogError("Tidepool authentication returned empty session token");
                    return (null, DateTime.MinValue);
                }

                // Parse response body for user ID
                var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var authResponse = JsonSerializer.Deserialize<TidepoolAuthResponse>(
                    jsonContent,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (authResponse == null || string.IsNullOrEmpty(authResponse.UserId))
                {
                    _logger.LogError("Tidepool authentication returned empty user ID");
                    return (null, DateTime.MinValue);
                }

                UserId = authResponse.UserId;
                var expiresAt = DateTime.UtcNow.AddHours(24);

                _logger.LogInformation(
                    "Tidepool authentication successful for user {UserId}, session expires at {ExpiresAt}",
                    UserId,
                    expiresAt);

                return (sessionToken, expiresAt);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    ex,
                    "HTTP error during Tidepool authentication attempt {Attempt}: {Message}",
                    attempt + 1,
                    ex.Message);

                if (attempt < maxRetries - 1)
                {
                    await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error during Tidepool authentication attempt {Attempt}",
                    attempt + 1);
                return (null, DateTime.MinValue);
            }
        }

        _logger.LogError("Tidepool authentication failed after {MaxRetries} attempts", maxRetries);
        return (null, DateTime.MinValue);
    }

    private class TidepoolAuthResponse
    {
        public string? UserId { get; set; }
    }
}
