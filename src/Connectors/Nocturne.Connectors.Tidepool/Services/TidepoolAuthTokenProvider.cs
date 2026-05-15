using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Tidepool.Configurations;
using Nocturne.Connectors.Tidepool.Models;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.Tidepool.Services;

/// <summary>
///     Token provider for Tidepool authentication.
///     Uses HTTP Basic Auth to login, extracts session token from response header.
/// </summary>
public class TidepoolAuthTokenProvider(
    HttpClient httpClient,
    IConnectorTokenCache tokenCache,
    IConnectorServerResolver<TidepoolConnectorConfiguration> serverResolver,
    ITenantAccessor tenantAccessor,
    ILogger<TidepoolAuthTokenProvider> logger,
    IRetryDelayStrategy retryDelayStrategy)
    : AuthTokenProviderBase<TidepoolConnectorConfiguration>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
{
    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    /// <summary>
    ///     Tidepool sessions last ~24 hours. Refresh at 23 hours.
    /// </summary>
    protected override int TokenLifetimeBufferMinutes => 60;

    protected override string ConnectorName => "Tidepool";

    protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        TidepoolConnectorConfiguration config, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        string? authUserId = null;

        var sessionToken = await ExecuteWithRetryAsync(
            async attempt =>
            {
                _logger.LogInformation(
                    "Authenticating with Tidepool for account: {Username} (attempt {Attempt}/{MaxRetries})",
                    config.Username,
                    attempt + 1,
                    maxRetries);

                var (token, userId) = await LoginAsync(config, cancellationToken);
                if (string.IsNullOrEmpty(token))
                    return (null, true);

                authUserId = userId;
                return (token, false);
            },
            _retryDelayStrategy,
            maxRetries,
            "Tidepool authentication",
            cancellationToken
        );

        if (string.IsNullOrEmpty(sessionToken))
            return (null, DateTime.MinValue, null);

        var resolvedUserId = !string.IsNullOrEmpty(config.UserId) ? config.UserId : authUserId;
        var expiresAt = DateTime.UtcNow.AddHours(24);
        _logger.LogInformation(
            "Tidepool authentication successful for user {UserId}, session expires at {ExpiresAt}",
            resolvedUserId,
            expiresAt);

        var metadata = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(resolvedUserId))
            metadata["UserId"] = resolvedUserId;

        return (sessionToken, expiresAt, metadata.Count > 0 ? metadata : null);
    }

    private async Task<(string? Token, string? UserId)> LoginAsync(TidepoolConnectorConfiguration config, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            _serverResolver.BuildUrl(config, "/auth/login"));

        // Tidepool uses HTTP Basic Authentication
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            await HandleErrorResponseAsync(response, "Tidepool authentication", cancellationToken);
            return (null, null);
        }

        // Session token is in the response header
        if (!response.Headers.TryGetValues(TidepoolConstants.Headers.SessionToken, out var tokenValues))
        {
            _logger.LogError("Tidepool authentication response missing {Header} header",
                TidepoolConstants.Headers.SessionToken);
            return (null, null);
        }

        var token = tokenValues.FirstOrDefault();
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogError("Tidepool authentication returned empty session token");
            return (null, null);
        }

        // Extract user ID from response body
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var authResponse = JsonSerializer.Deserialize<TidepoolAuthResponse>(body, JsonDefaults.CaseInsensitive);

        string? userId = null;
        if (authResponse != null && !string.IsNullOrEmpty(authResponse.Userid))
        {
            userId = authResponse.Userid;
            _logger.LogDebug("Tidepool user ID resolved to {UserId}", userId);
        }
        else
        {
            _logger.LogWarning("Tidepool authentication response did not contain a user ID");
        }

        return (token, userId);
    }
}
