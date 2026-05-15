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

    private string? _userId;

    /// <summary>
    ///     The authenticated user ID, used for data fetching endpoints.
    ///     Set from auth response unless overridden in configuration.
    /// </summary>
    public string? UserId => _userId;

    /// <summary>
    ///     Tidepool sessions last ~24 hours. Refresh at 23 hours.
    /// </summary>
    protected override int TokenLifetimeBufferMinutes => 60;

    protected override string ConnectorName => "Tidepool";

    protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        TidepoolConnectorConfiguration config, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;

        var sessionToken = await ExecuteWithRetryAsync(
            async attempt =>
            {
                _logger.LogInformation(
                    "Authenticating with Tidepool for account: {Username} (attempt {Attempt}/{MaxRetries})",
                    config.Username,
                    attempt + 1,
                    maxRetries);

                var token = await LoginAsync(config, cancellationToken);
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
            return (null, DateTime.MinValue, null);

        var resolvedUserId = !string.IsNullOrEmpty(config.UserId) ? config.UserId : _userId;
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

    private async Task<string?> LoginAsync(TidepoolConnectorConfiguration config, CancellationToken cancellationToken)
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
