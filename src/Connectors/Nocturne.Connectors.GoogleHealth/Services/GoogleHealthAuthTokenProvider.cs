using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Models;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.GoogleHealth.Services;

public sealed class GoogleHealthAuthTokenProvider(
    HttpClient httpClient,
    IConnectorTokenCache tokenCache,
    IConnectorServerResolver<GoogleHealthConnectorConfiguration> serverResolver,
    ITenantAccessor tenantAccessor,
    ILogger<GoogleHealthAuthTokenProvider> logger)
    : AuthTokenProviderBase<GoogleHealthConnectorConfiguration>(
        httpClient,
        tokenCache,
        serverResolver,
        tenantAccessor,
        logger)
{
    private const string RefreshTokenKey = "RefreshToken";
    private const string ScopesKey = "Scopes";
    private const string ExpiresAtKey = "AccessTokenExpiresAt";
    private readonly GoogleHealthOAuthClient oauth = new(httpClient);
    private readonly ConcurrentDictionary<Guid, GoogleHealthTokenSession> storedSessions = new();

    protected override string ConnectorName => "GoogleHealth";
    protected override int TokenLifetimeBufferMinutes => 1;
    protected override bool RethrowTokenAcquisitionExceptions => true;

    private Guid TenantId => _tenantAccessor.IsResolved
        ? _tenantAccessor.TenantId
        : throw new InvalidOperationException("Google Health token storage requires a resolved tenant context");

    public async Task SeedSessionAsync(GoogleHealthTokenSession session)
    {
        var tenantId = TenantId;
        var cached = await _tokenCache.GetAsync(ConnectorName, tenantId);
        if (cached is not null)
        {
            storedSessions[tenantId] = FromCache(cached);
            return;
        }

        storedSessions[tenantId] = session;
        if (!string.IsNullOrWhiteSpace(session.AccessToken) && session.AccessTokenExpiresAt is not null)
            await StoreSessionAsync(session);
    }

    public async Task StoreSessionAsync(GoogleHealthTokenSession session)
    {
        if (string.IsNullOrWhiteSpace(session.AccessToken) || session.AccessTokenExpiresAt is null)
            throw new ArgumentException("A Google Health access token and expiration are required.", nameof(session));

        var tenantId = TenantId;
        storedSessions[tenantId] = session;
        await _tokenCache.SetAsync(
            ConnectorName,
            tenantId,
            new ConnectorSession(
                session.AccessToken,
                session.AccessTokenExpiresAt.Value.UtcDateTime.AddMinutes(-TokenLifetimeBufferMinutes),
                Metadata(session)));
    }

    public async Task<GoogleHealthTokenSession?> GetCurrentSessionAsync()
    {
        var cached = await GetCachedSessionAsync();
        return cached is null ? null : FromCache(cached);
    }

    public async Task<GoogleHealthTokenSession> ExchangeAuthorizationCodeAsync(
        GoogleHealthConnectorConfiguration configuration,
        string code,
        string verifier,
        IReadOnlyCollection<string> requestedScopes,
        CancellationToken cancellationToken)
    {
        var response = await oauth.ExchangeAuthorizationCodeAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["client_id"] = configuration.ClientId,
            ["client_secret"] = configuration.ClientSecret!,
            ["redirect_uri"] = configuration.CallbackUrl
        }, cancellationToken);

        return Parse(response, "authorization_code", null, requestedScopes, requireRefreshToken: true);
    }

    public Task<bool> RevokeAsync(string refreshToken, CancellationToken cancellationToken) =>
        oauth.RevokeAsync(refreshToken, cancellationToken);

    public Task<string> AccountKeyAsync(string accessToken, CancellationToken cancellationToken) =>
        oauth.AccountKeyAsync(accessToken, cancellationToken);

    protected override async Task<(
        string? Token,
        DateTime ExpiresAt,
        IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        GoogleHealthConnectorConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        storedSessions.TryGetValue(tenantId, out var stored);
        var refreshToken = stored?.RefreshToken ?? configuration.RefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
            return (null, DateTime.MinValue, null);

        var response = await oauth.RefreshAccessTokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = configuration.ClientId,
            ["client_secret"] = configuration.ClientSecret!
        }, cancellationToken);
        var session = Parse(response, "token_refresh", refreshToken, stored?.Scopes ?? []);
        storedSessions[tenantId] = session;

        return (
            session.AccessToken,
            session.AccessTokenExpiresAt!.Value.UtcDateTime,
            Metadata(session));
    }

    private static GoogleHealthTokenSession Parse(
        JsonElement response,
        string stage,
        string? fallbackRefreshToken,
        IReadOnlyCollection<string> fallbackScopes,
        bool requireRefreshToken = false)
    {
        ValidateTokenType(response, stage);
        var accessToken = RequiredString(response, "access_token", stage);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(RequiredExpiresIn(response, stage));
        var scopes = ResponseScopes(response, fallbackScopes);
        var refreshToken = fallbackRefreshToken;

        if (response.TryGetProperty("refresh_token", out var replacement))
        {
            if (replacement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(replacement.GetString()))
                throw new GoogleHealthException("invalid_token_response", stage: stage);
            refreshToken = replacement.GetString();
        }

        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new GoogleHealthException(
                requireRefreshToken ? "offline_access_required" : "invalid_token_response",
                stage: stage);

        return new GoogleHealthTokenSession(refreshToken, scopes, accessToken, expiresAt);
    }

    private static string RequiredString(JsonElement response, string name, string stage)
    {
        if (!response.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new GoogleHealthException("invalid_token_response", stage: stage);
        return value.GetString()!;
    }

    private static int RequiredExpiresIn(JsonElement response, string stage)
    {
        if (!response.TryGetProperty("expires_in", out var value) ||
            !value.TryGetInt32(out var seconds) ||
            seconds <= 0)
            throw new GoogleHealthException("invalid_token_response", stage: stage);
        return seconds;
    }

    private static void ValidateTokenType(JsonElement response, string stage)
    {
        if (response.TryGetProperty("token_type", out var value) &&
            (value.ValueKind != JsonValueKind.String ||
             !string.Equals(value.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)))
            throw new GoogleHealthException("invalid_token_response", stage: stage);
    }

    private static string[] ResponseScopes(JsonElement response, IReadOnlyCollection<string> fallback)
    {
        if (!response.TryGetProperty("scope", out var value) || value.ValueKind != JsonValueKind.String)
            return fallback.Distinct(StringComparer.Ordinal).ToArray();
        return (value.GetString() ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<string, string> Metadata(GoogleHealthTokenSession session) => new()
    {
        [RefreshTokenKey] = session.RefreshToken,
        [ScopesKey] = JsonSerializer.Serialize(session.Scopes),
        [ExpiresAtKey] = session.AccessTokenExpiresAt!.Value.ToString("O", CultureInfo.InvariantCulture)
    };

    private static GoogleHealthTokenSession FromCache(ConnectorSession cached)
    {
        if (cached.Metadata is null ||
            !cached.Metadata.TryGetValue(RefreshTokenKey, out var refreshToken) ||
            !cached.Metadata.TryGetValue(ScopesKey, out var serializedScopes) ||
            !cached.Metadata.TryGetValue(ExpiresAtKey, out var serializedExpiresAt) ||
            JsonSerializer.Deserialize<string[]>(serializedScopes) is not { } scopes ||
            !DateTimeOffset.TryParseExact(
                serializedExpiresAt,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresAt))
            throw new GoogleHealthException("invalid_token_response", stage: "token_cache");

        return new GoogleHealthTokenSession(refreshToken, scopes, cached.Token, expiresAt);
    }
}
