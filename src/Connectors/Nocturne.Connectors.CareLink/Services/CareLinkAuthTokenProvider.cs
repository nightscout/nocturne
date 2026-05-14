using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.CareLink.Configurations;

namespace Nocturne.Connectors.CareLink.Services;

/// <summary>
/// Token provider for CareLink authentication.
/// Attempts refresh token grant first, falling back to full Auth0 PKCE credential login.
/// </summary>
public class CareLinkAuthTokenProvider(
    IOptions<CareLinkConnectorConfiguration> config,
    HttpClient httpClient,
    ILogger<CareLinkAuthTokenProvider> logger,
    IRetryDelayStrategy retryDelayStrategy)
    : AuthTokenProviderBase<CareLinkConnectorConfiguration>(config.Value, httpClient, logger)
{
    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    protected override int TokenLifetimeBufferMinutes => 1;

    private string? _refreshToken;
    private string? _clientId;
    private string? _tokenUrl;
    private string? _audience;
    private readonly object _stateLock = new();

    public string? CurrentRefreshToken { get { lock (_stateLock) return _refreshToken; } }
    public string? CurrentClientId { get { lock (_stateLock) return _clientId; } }
    public string? CurrentTokenUrl { get { lock (_stateLock) return _tokenUrl; } }
    public string? CurrentAudience { get { lock (_stateLock) return _audience; } }

    /// <summary>
    /// Seeds persisted token state (refresh token, client ID, token URL, audience) into the provider.
    /// Called by the connector service on startup to restore state from secrets storage.
    /// </summary>
    public void InitializeFromSecrets(string? refreshToken, string? clientId, string? tokenUrl, string? audience)
    {
        lock (_stateLock)
        {
            _refreshToken = refreshToken;
            _clientId = clientId;
            _tokenUrl = tokenUrl;
            _audience = audience;
        }
    }

    protected override async Task<(string? Token, DateTime ExpiresAt)> AcquireTokenAsync(CancellationToken cancellationToken)
    {
        // Try refresh first
        string? refreshToken, clientId, tokenUrl;
        lock (_stateLock)
        {
            refreshToken = _refreshToken ?? _config.RefreshToken;
            clientId = _clientId;
            tokenUrl = _tokenUrl;
        }

        if (!string.IsNullOrEmpty(refreshToken) && !string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(tokenUrl))
        {
            var refreshResult = await TryRefreshTokenAsync(refreshToken, clientId, tokenUrl, cancellationToken);
            if (refreshResult != null) return refreshResult.Value;
            _logger.LogWarning("Refresh token failed, falling back to credential login");
        }

        // Credential login fallback
        if (string.IsNullOrEmpty(_config.Password))
        {
            _logger.LogError(
                "Cannot authenticate: refresh token is invalid/expired and no password is configured. " +
                "Please provide a valid password or a new refresh token.");
            return (null, DateTime.MinValue);
        }

        const int maxRetries = 2;
        var result = await ExecuteWithRetryAsync(
            async attempt =>
            {
                _logger.LogInformation("Performing CareLink credential login for {Username} (attempt {Attempt}/{Max})",
                    _config.Username, attempt + 1, maxRetries);

                using var authFlow = new CareLinkAuthFlowService(_logger);
                var authResult = await authFlow.LoginAsync(_config.Username, _config.Password!, _config.Server, cancellationToken);
                return (authResult, authResult == null);
            },
            _retryDelayStrategy, maxRetries, "CareLink credential login", cancellationToken);

        if (result == null) return (null, DateTime.MinValue);

        lock (_stateLock)
        {
            _refreshToken = result.RefreshToken;
            _clientId = result.ClientId;
            _tokenUrl = result.TokenUrl;
            _audience = result.Audience;
        }

        return (result.AccessToken, GetTokenExpiry(result.AccessToken));
    }

    private async Task<(string Token, DateTime ExpiresAt)?> TryRefreshTokenAsync(
        string refreshToken, string clientId, string tokenUrl, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken,
            });

            var response = await _httpClient.PostAsync(tokenUrl, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Token refresh returned {StatusCode}: {Body}", response.StatusCode, body);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var accessToken = root.GetProperty("access_token").GetString();
            var newRefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

            if (string.IsNullOrEmpty(accessToken)) return null;

            if (!string.IsNullOrEmpty(newRefreshToken))
                lock (_stateLock) { _refreshToken = newRefreshToken; }

            var expiresAt = GetTokenExpiry(accessToken);
            _logger.LogInformation("CareLink token refreshed, expires at {ExpiresAt}", expiresAt);
            return (accessToken, expiresAt);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Token refresh failed with exception");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Token refresh failed with exception");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Token refresh failed with exception");
            return null;
        }
    }

    private static DateTime GetTokenExpiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) return DateTime.UtcNow.AddHours(1);

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var payloadBytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(payloadBytes);
            if (doc.RootElement.TryGetProperty("exp", out var exp))
                return DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()).UtcDateTime;
        }
        catch (FormatException) { /* fall through */ }
        catch (JsonException) { /* fall through */ }
        catch (InvalidOperationException) { /* fall through */ }
        catch (ArgumentException) { /* fall through */ }

        return DateTime.UtcNow.AddHours(1);
    }
}
