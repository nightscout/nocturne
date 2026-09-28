using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Extensions;
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

        // A refresh token obtained outside the connect flow — pasted into the connector settings after
        // being minted by an external tool — arrives on its own. The client id and token endpoint it
        // must be redeemed against are public values in CareLink's discovery config, so resolve them
        // instead of requiring the user to supply them.
        if (!string.IsNullOrEmpty(refreshToken) && (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(tokenUrl)))
        {
            using var discovery = CreateAuthFlow();
            var sso = await discovery.ResolveSsoParametersAsync(config.Server, cancellationToken);
            if (sso != null)
            {
                clientId = string.IsNullOrEmpty(clientId) ? sso.ClientId : clientId;
                tokenUrl = string.IsNullOrEmpty(tokenUrl) ? sso.TokenUrl : tokenUrl;
                audience = string.IsNullOrEmpty(audience) ? sso.Audience : audience;
            }
        }

        if (!string.IsNullOrEmpty(refreshToken) && !string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(tokenUrl))
        {
            var refresh = await TryRefreshTokenAsync(
                refreshToken, clientId, tokenUrl, LoginAttempts(config), cancellationToken);

            if (refresh.Outcome == RefreshOutcome.Success)
            {
                if (!string.IsNullOrEmpty(refresh.NewRefreshToken))
                    refreshToken = refresh.NewRefreshToken;
                return (refresh.Token, refresh.ExpiresAt, BuildMetadata(refreshToken, clientId, tokenUrl, audience));
            }

            // Replaying the member's password against Auth0 risks a CAPTCHA and lockout, so a token
            // endpoint that is only unwell ends the acquisition here.
            if (refresh.Outcome == RefreshOutcome.Unavailable)
            {
                _logger.LogWarning(
                    "CareLink token refresh was unavailable and no credential login was attempted");
                return (null, DateTime.MinValue, null);
            }

            _logger.LogWarning("Refresh token rejected, falling back to credential login");
        }

        // Credential login fallback
        if (string.IsNullOrEmpty(config.Password))
        {
            _logger.LogError(
                "Cannot authenticate: refresh token is invalid/expired and no password is configured. " +
                "Please provide a valid password or a new refresh token.");
            return (null, DateTime.MinValue, null);
        }

        var maxRetries = LoginAttempts(config);
        var result = await ExecuteWithRetryAsync(
            async attempt =>
            {
                _logger.LogInformation("Performing CareLink credential login for {Username} (attempt {Attempt}/{Max})",
                    config.Username, attempt + 1, maxRetries);

                using var authFlow = CreateAuthFlow();
                var login = await authFlow.LoginAsync(config.Username, config.Password!, config.Server, cancellationToken);
                return (login.Result, login.ShouldRetry);
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

    /// <summary>
    /// Creates the flow service used for discovery and credential login. Overridden in tests to
    /// supply a fake HTTP handler.
    /// </summary>
    protected virtual CareLinkAuthFlowService CreateAuthFlow() => new(_logger);

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

    /// <summary>
    /// What a refresh said about the token. Auth0 answers a revoked or expired refresh token with a 400
    /// or a 403, so every failure a repeat cannot clear counts as <see cref="RefreshOutcome.Rejected"/>
    /// and falls through to the password login, as before. Only a transient failure that outlasts the
    /// retry budget ends as <see cref="RefreshOutcome.Unavailable"/>, which must not replay the password.
    /// </summary>
    private enum RefreshOutcome
    {
        Success,
        Rejected,
        Transient,
        Unavailable,
    }

    private sealed record RefreshResult(
        RefreshOutcome Outcome, string? Token, DateTime ExpiresAt, string? NewRefreshToken);

    /// <summary>
    /// Redeems the refresh token, retrying a transient token-endpoint failure on the same loop and
    /// delay strategy as the credential login.
    /// </summary>
    private async Task<RefreshResult> TryRefreshTokenAsync(
        string refreshToken, string clientId, string tokenUrl, int maxAttempts, CancellationToken ct)
    {
        var result = await ConnectorRetryLoop.RunAsync<RefreshResult>(
            async (_, _) =>
            {
                var attempt = await AttemptRefreshAsync(refreshToken, clientId, tokenUrl, ct);
                return attempt.Outcome == RefreshOutcome.Transient
                    ? RetryStep<RefreshResult>.RetryAfterDelay
                    : RetryStep<RefreshResult>.Complete(attempt);
            },
            _retryDelayStrategy,
            maxAttempts,
            _ => new RefreshResult(RefreshOutcome.Unavailable, null, DateTime.MinValue, null),
            ct);

        return result!;
    }

    private async Task<RefreshResult> AttemptRefreshAsync(
        string refreshToken, string clientId, string tokenUrl, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken,
            });

            response = await _httpClient.PostAsync(tokenUrl, content, ct);
        }
        // A HttpClient timeout arrives as a cancellation that the caller did not ask for, so it is a
        // transient failure; a real cancellation is left to propagate.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("CareLink token refresh timed out");
            return new RefreshResult(RefreshOutcome.Transient, null, DateTime.MinValue, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "CareLink token refresh could not reach the token endpoint");
            return new RefreshResult(RefreshOutcome.Transient, null, DateTime.MinValue, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Token refresh returned {StatusCode}: {Body}", response.StatusCode, body);

            var outcome = response.IsRetryableError() ? RefreshOutcome.Transient : RefreshOutcome.Rejected;
            return new RefreshResult(outcome, null, DateTime.MinValue, null);
        }

        try
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            var newRefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

            if (string.IsNullOrEmpty(accessToken))
                return new RefreshResult(RefreshOutcome.Rejected, null, DateTime.MinValue, null);

            var expiresAt = GetTokenExpiry(accessToken);
            _logger.LogInformation("CareLink token refreshed, expires at {ExpiresAt}", expiresAt);
            return new RefreshResult(RefreshOutcome.Success, accessToken, expiresAt, newRefreshToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "CareLink token refresh response could not be parsed");
            return new RefreshResult(RefreshOutcome.Rejected, null, DateTime.MinValue, null);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "CareLink token refresh response was malformed");
            return new RefreshResult(RefreshOutcome.Rejected, null, DateTime.MinValue, null);
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
