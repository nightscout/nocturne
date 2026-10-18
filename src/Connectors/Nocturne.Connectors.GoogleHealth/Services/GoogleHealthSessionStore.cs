using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Models;
using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.Connectors.GoogleHealth.Services;

/// <summary>
/// A tenant's Google session: the refresh token, its grant and the client it was issued to, kept
/// in the connector's encrypted secrets, plus the cached access token.
/// </summary>
public sealed class GoogleHealthSessionStore(
    IConnectorConfigurationService configurations,
    GoogleHealthAuthTokenProvider oauth,
    IOptions<GoogleHealthPlatformOptions> platform)
{
    private const string ConnectorName = "GoogleHealth";
    private const string RefreshTokenKey = "refreshToken";
    private const string GrantedScopesKey = "grantedScopes";
    private const string AccountKey = "accountKey";
    // Read back as GoogleHealthConnectorConfiguration.SessionClientId.
    private const string SessionClientIdKey = "clientId";
    private const string LegacyClientSecretKey = "clientSecret";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Whether the stored refresh token belongs to a client other than the instance's: a former
    /// per-tenant client, or one the operator replaced. Google refuses it, so only a new sign-in
    /// recovers.
    /// </summary>
    public bool IssuedToAnotherClient(GoogleHealthConnectorConfiguration configuration) =>
        platform.Value.IsConfigured &&
        !string.IsNullOrWhiteSpace(configuration.RefreshToken) &&
        !string.Equals(configuration.SessionClientId, platform.Value.ClientId, StringComparison.Ordinal);

    /// <summary>The stored session, with its cached access token when that is still current.</summary>
    public async Task<GoogleHealthTokenSession?> CurrentAsync(GoogleHealthConnectorConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.RefreshToken)) return null;
        await oauth.DiscardStaleCacheAsync(configuration);
        try
        {
            if (await oauth.GetCurrentSessionAsync() is { } cached) return cached;
        }
        catch (GoogleHealthException)
        {
            oauth.InvalidateToken();
        }
        return new GoogleHealthTokenSession(configuration.RefreshToken, configuration.ParsedGrantedScopes());
    }

    /// <summary>
    /// A session with a usable access token, refreshing it when the cached one is missing or
    /// <paramref name="force"/> is set, and storing a rotated refresh token or grant.
    /// </summary>
    public async Task<GoogleHealthTokenSession> RefreshAsync(
        GoogleHealthConnectorConfiguration configuration, bool force, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configuration.RefreshToken))
            throw new GoogleHealthException("reconnect_required", stage: "session_read");
        if (IssuedToAnotherClient(configuration))
            throw new GoogleHealthException("client_changed", stage: "session_read");
        await oauth.DiscardStaleCacheAsync(configuration);
        if (force) oauth.InvalidateToken();
        if (string.IsNullOrWhiteSpace(await oauth.GetValidTokenAsync(configuration, ct)))
            throw new GoogleHealthException("invalid_token_response", stage: "token_refresh");
        var session = await oauth.GetCurrentSessionAsync() ??
            throw new GoogleHealthException("invalid_token_response", stage: "token_cache");

        var scopes = string.Join(' ', session.Scopes.Distinct(StringComparer.Ordinal));
        if (session.RefreshToken != configuration.RefreshToken || scopes != configuration.GrantedScopes)
        {
            var secrets = await configurations.GetSecretsAsync(ConnectorName, ct);
            secrets[RefreshTokenKey] = session.RefreshToken;
            secrets[GrantedScopesKey] = scopes;
            await configurations.SaveSecretsAsync(ConnectorName, secrets, ct: ct);
            configuration.RefreshToken = session.RefreshToken;
            configuration.GrantedScopes = scopes;
            // Saving the secrets invalidated the cached access token.
            await oauth.StoreSessionAsync(session);
        }
        return session;
    }

    /// <summary>
    /// Runs <paramref name="read"/> with <paramref name="accessToken"/>; if Google rejects it,
    /// refreshes once and retries, and a second rejection means the grant is gone.
    /// </summary>
    public async Task<T> WithAccessTokenAsync<T>(
        GoogleHealthConnectorConfiguration configuration,
        string accessToken,
        Func<string, Task<T>> read,
        CancellationToken ct,
        Func<Task>? beforeRetry = null)
    {
        try
        {
            return await read(accessToken);
        }
        catch (GoogleHealthException first) when (first.Message == "access_token_rejected")
        {
            if (beforeRetry is not null) await beforeRetry();
            var refreshed = await RefreshAsync(configuration, force: true, ct);
            try
            {
                return await read(refreshed.AccessToken!);
            }
            catch (GoogleHealthException second) when (second.Message == "access_token_rejected")
            {
                throw new GoogleHealthException("reconnect_required", stage: second.Stage,
                    dataType: second.DataType, providerReason: second.ProviderReason,
                    providerStatus: second.ProviderStatus);
            }
        }
    }

    public async Task SaveAsync(GoogleHealthTokenSession session, string accountKey, Guid subject, CancellationToken ct)
    {
        var secrets = await configurations.GetSecretsAsync(ConnectorName, ct);
        secrets.Remove(LegacyClientSecretKey);
        secrets[SessionClientIdKey] = platform.Value.ClientId;
        secrets[RefreshTokenKey] = session.RefreshToken;
        secrets[GrantedScopesKey] = string.Join(' ', session.Scopes.Distinct(StringComparer.Ordinal));
        secrets[AccountKey] = accountKey;
        await configurations.SaveSecretsAsync(ConnectorName, secrets, subject.ToString(), ct);
        await SetEnabledAsync(true, subject.ToString(), ct);
        await oauth.StoreSessionAsync(session);
    }

    /// <param name="removeAccount">
    /// Also forget which Google account was connected, which is what lets a different account
    /// connect afterwards.
    /// </param>
    public async Task ClearAsync(bool removeAccount, Guid? subject, CancellationToken ct)
    {
        oauth.InvalidateToken();
        await SetEnabledAsync(false, subject?.ToString(), ct);
        var secrets = await configurations.GetSecretsAsync(ConnectorName, ct);
        secrets.Remove(RefreshTokenKey);
        secrets.Remove(GrantedScopesKey);
        secrets.Remove(SessionClientIdKey);
        secrets.Remove(LegacyClientSecretKey);
        if (removeAccount) secrets.Remove(AccountKey);
        await configurations.SaveSecretsAsync(ConnectorName, secrets, subject?.ToString(), ct);
    }

    public async Task<string?> AccountKeyAsync(CancellationToken ct) =>
        (await configurations.GetSecretsAsync(ConnectorName, ct)).GetValueOrDefault(AccountKey);

    private async Task SetEnabledAsync(bool enabled, string? modifiedBy, CancellationToken ct)
    {
        var stored = await configurations.GetConfigurationAsync(ConnectorName, ct);
        if (stored?.Configuration is null) return;
        var configuration = JsonNode.Parse(stored.Configuration.RootElement.GetRawText())!.AsObject();
        configuration["enabled"] = enabled;
        using var document = JsonDocument.Parse(configuration.ToJsonString(Json));
        await configurations.SaveConfigurationAsync(ConnectorName, document, modifiedBy, ct);
    }
}
