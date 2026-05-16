using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.CareLink.Configurations;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.CareLink.Services;

/// <summary>
/// Token provider for CareLink authentication.
/// Attempts refresh token grant first, falling back to full Auth0 PKCE credential login.
/// </summary>
public class CareLinkAuthTokenProvider(
    HttpClient httpClient,
    IConnectorTokenCache tokenCache,
    IConnectorServerResolver<CareLinkConnectorConfiguration> serverResolver,
    ITenantAccessor tenantAccessor,
    ILogger<CareLinkAuthTokenProvider> logger,
    IRetryDelayStrategy retryDelayStrategy)
    : AuthTokenProviderBase<CareLinkConnectorConfiguration>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
{
    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    protected override int TokenLifetimeBufferMinutes => 1;

    protected override string ConnectorName => "CareLink";

    /// <summary>
    ///     Per-tenant state seeded by <see cref="InitializeFromSecrets"/>.
    ///     Only used as a fallback when the token cache has no prior session for this tenant.
    ///     Keyed by tenant ID so concurrent tenant syncs cannot stomp each other.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, TenantSecrets> _tenantSecrets = new();

    public string? CurrentRefreshToken => GetTenantSecrets()?.RefreshToken;
    public string? CurrentClientId => GetTenantSecrets()?.ClientId;
    public string? CurrentTokenUrl => GetTenantSecrets()?.TokenUrl;
    public string? CurrentAudience => GetTenantSecrets()?.Audience;

    /// <summary>
    /// Seeds persisted token state (refresh token, client ID, token URL, audience) into the provider.
    /// Called by the connector service per-tenant before GetValidTokenAsync.
    /// Keyed by tenant ID so concurrent tenant syncs cannot stomp each other.
    /// </summary>
    public void InitializeFromSecrets(string? refreshToken, string? clientId, string? tokenUrl, string? audience)
    {
        var tenantId = _tenantAccessor.TenantId;
        _tenantSecrets[tenantId] = new TenantSecrets(refreshToken, clientId, tokenUrl, audience);
    }

    private TenantSecrets? GetTenantSecrets()
    {
        return _tenantSecrets.TryGetValue(_tenantAccessor.TenantId, out var secrets) ? secrets : null;
    }

    private sealed record TenantSecrets(string? RefreshToken, string? ClientId, string? TokenUrl, string? Audience);

    protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        CareLinkConnectorConfiguration config, CancellationToken cancellationToken)
    {
        // Read from previously cached session metadata first, fall back to seeded secrets
        var cached = await _tokenCache.GetAsync(ConnectorName, _tenantAccessor.TenantId);
        var seeded = GetTenantSecrets();
        var refreshToken = cached?.Metadata?.GetValueOrDefault("RefreshToken") ?? seeded?.RefreshToken ?? config.RefreshToken;
        var clientId = cached?.Metadata?.GetValueOrDefault("ClientId") ?? seeded?.ClientId;
        var tokenUrl = cached?.Metadata?.GetValueOrDefault("TokenUrl") ?? seeded?.TokenUrl;

        var audience = cached?.Metadata?.GetValueOrDefault("Audience") ?? seeded?.Audience;

        if (!string.IsNullOrEmpty(refreshToken) && !string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(tokenUrl))
        {
            var refreshResult = await TryRefreshTokenAsync(refreshToken, clientId, tokenUrl, cancellationToken);
            if (refreshResult != null)
            {
                var (token, expiresAt, newRefreshToken) = refreshResult.Value;
                if (!string.IsNullOrEmpty(newRefreshToken))
                    refreshToken = newRefreshToken;
                return (token, expiresAt, BuildMetadata(refreshToken, clientId, tokenUrl, audience));
            }
            _logger.LogWarning("Refresh token failed, falling back to credential login");
        }

        // Credential login fallback
        if (string.IsNullOrEmpty(config.Password))
        {
            _logger.LogError(
                "Cannot authenticate: refresh token is invalid/expired and no password is configured. " +
                "Please provide a valid password or a new refresh token.");
            return (null, DateTime.MinValue, null);
        }

        const int maxRetries = 2;
        var result = await ExecuteWithRetryAsync(
            async attempt =>
            {
                _logger.LogInformation("Performing CareLink credential login for {Username} (attempt {Attempt}/{Max})",
                    config.Username, attempt + 1, maxRetries);

                using var authFlow = new CareLinkAuthFlowService(_logger);
                var authResult = await authFlow.LoginAsync(config.Username, config.Password!, config.Server, cancellationToken);
                return (authResult, authResult == null);
            },
            _retryDelayStrategy, maxRetries, "CareLink credential login", cancellationToken);

        if (result == null) return (null, DateTime.MinValue, null);

        refreshToken = result.RefreshToken;
        clientId = result.ClientId;
        tokenUrl = result.TokenUrl;
        audience = result.Audience;

        return (result.AccessToken, GetTokenExpiry(result.AccessToken),
            BuildMetadata(refreshToken, clientId, tokenUrl, audience));
    }

    private static IReadOnlyDictionary<string, string>? BuildMetadata(
        string? refreshToken, string? clientId, string? tokenUrl, string? audience)
    {
        var metadata = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(refreshToken))
            metadata["RefreshToken"] = refreshToken;
        if (!string.IsNullOrEmpty(clientId))
            metadata["ClientId"] = clientId;
        if (!string.IsNullOrEmpty(tokenUrl))
            metadata["TokenUrl"] = tokenUrl;
        if (!string.IsNullOrEmpty(audience))
            metadata["Audience"] = audience;
        return metadata.Count > 0 ? metadata : null;
    }

    private async Task<(string Token, DateTime ExpiresAt, string? NewRefreshToken)?> TryRefreshTokenAsync(
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

            var expiresAt = GetTokenExpiry(accessToken);
            _logger.LogInformation("CareLink token refreshed, expires at {ExpiresAt}", expiresAt);
            return (accessToken, expiresAt, newRefreshToken);
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
