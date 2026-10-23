using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Models;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.GoogleHealth.Services;

/// <summary>
/// A tenant's Google session: the refresh token, its grant and the client it was issued to, kept
/// in the connector's encrypted secrets, plus the cached access token.
/// </summary>
/// <param name="revokeTimeout">How long a best-effort revocation may wait for Google; five seconds unless set.</param>
public sealed class GoogleHealthSessionStore(
    IConnectorConfigurationService configurations,
    GoogleHealthAuthTokenProvider oauth,
    IGoogleHealthGrantHolders holders,
    ITenantAccessor tenants,
    IOptions<GoogleHealthPlatformOptions> platform,
    ILogger<GoogleHealthSessionStore> logger,
    TimeSpan? revokeTimeout = null)
{
    private const string ConnectorName = "GoogleHealth";
    private const string RefreshTokenKey = "refreshToken";
    private const string GrantedScopesKey = "grantedScopes";
    private const string AccountKey = "accountKey";
    // Read back as GoogleHealthConnectorConfiguration.SessionClientId.
    private const string SessionClientIdKey = "clientId";
    private const string LegacyClientSecretKey = "clientSecret";

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
        if (configuration.IssuedToAnotherClient(platform.Value))
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

    /// <summary>
    /// The account a tenant's stored secrets hold a refresh token for, or null when they hold no
    /// token or do not name the account.
    /// </summary>
    public static string? HeldAccount(IReadOnlyDictionary<string, string> secrets) =>
        !string.IsNullOrWhiteSpace(secrets.GetValueOrDefault(RefreshTokenKey)) &&
        secrets.GetValueOrDefault(AccountKey) is { Length: > 0 } account
            ? account
            : null;

    /// <summary>Once this returns, the grant is stored and must not be revoked by a later failure.</summary>
    /// <remarks>The account is held before the token is saved, so no stored token is ever unheld.</remarks>
    public async Task StoreAsync(GoogleHealthTokenSession session, string accountKey, Guid subject, CancellationToken ct)
    {
        var secrets = await configurations.GetSecretsAsync(ConnectorName, ct);
        await holders.HoldAsync(TenantId, accountKey, ct);
        secrets.Remove(LegacyClientSecretKey);
        secrets[SessionClientIdKey] = platform.Value.ClientId;
        secrets[RefreshTokenKey] = session.RefreshToken;
        secrets[GrantedScopesKey] = string.Join(' ', session.Scopes.Distinct(StringComparer.Ordinal));
        secrets[AccountKey] = accountKey;
        await configurations.SaveSecretsAsync(ConnectorName, secrets, subject.ToString(), ct);
    }

    /// <summary>
    /// Caches the access token of a session <see cref="StoreAsync"/> has stored. Not cancellable:
    /// the stored session is already live.
    /// </summary>
    public Task CacheAsync(GoogleHealthTokenSession session) => oauth.StoreSessionAsync(session);

    /// <param name="removeAccount">
    /// Also forget which Google account was connected, which is what lets a different account
    /// connect afterwards.
    /// </param>
    /// <param name="revoke">Also revoke the stored refresh token (<see cref="RevokeAsync"/>).</param>
    public async Task ClearAsync(bool removeAccount, Guid? subject, bool revoke, CancellationToken ct)
    {
        oauth.InvalidateToken();
        var secrets = await configurations.GetSecretsAsync(ConnectorName, ct);
        var refreshToken = secrets.GetValueOrDefault(RefreshTokenKey);
        var account = secrets.GetValueOrDefault(AccountKey);
        secrets.Remove(RefreshTokenKey);
        secrets.Remove(GrantedScopesKey);
        secrets.Remove(SessionClientIdKey);
        secrets.Remove(LegacyClientSecretKey);
        if (removeAccount) secrets.Remove(AccountKey);
        await configurations.SaveSecretsAsync(ConnectorName, secrets, subject?.ToString(), ct);
        // Released before the revocation asks who else holds the account, so two tenants clearing
        // at once cannot each see the other and both keep the grant.
        await holders.ReleaseAsync(TenantId, ct);
        if (revoke && !string.IsNullOrEmpty(refreshToken))
            await RevokeAsync(refreshToken, account, ct);
    }

    /// <summary>
    /// Drops the tenant's hold after a sign-in whose grant was never stored, unless a token is
    /// stored after all (<see cref="StoreAsync"/> holds before it saves).
    /// </summary>
    public async Task ReleaseUnstoredAsync(CancellationToken ct)
    {
        if (HeldAccount(await configurations.GetSecretsAsync(ConnectorName, ct)) is null)
            await holders.ReleaseAsync(TenantId, ct);
    }

    /// <summary>
    /// Asks Google to revoke <paramref name="refreshToken"/>'s grant unless another tenant holds a
    /// token for the same account (<see cref="IGoogleHealthGrantHolders"/>), or that cannot be told
    /// because the account is unknown or the holders cannot be read. Gives up after the revoke
    /// timeout; revocation needs no client credentials.
    /// </summary>
    public async Task<GoogleHealthRevocation> RevokeAsync(string refreshToken, string? accountKey, CancellationToken ct)
    {
        var sharing = string.IsNullOrEmpty(accountKey)
            ? GoogleHealthGrantSharing.Undetermined
            : await holders.SharingAsync(TenantId, accountKey, ct);
        switch (sharing)
        {
            case GoogleHealthGrantSharing.Shared:
                return GoogleHealthRevocation.Shared;
            case GoogleHealthGrantSharing.Undetermined:
                logger.LogWarning(
                    "Google Health kept a grant of tenant {TenantId} because it could not tell whether another tenant holds its account",
                    TenantId);
                return GoogleHealthRevocation.Undetermined;
            default:
                return await TryRevokeAsync(refreshToken, ct) ? GoogleHealthRevocation.Revoked : GoogleHealthRevocation.Failed;
        }
    }

    private async Task<bool> TryRevokeAsync(string refreshToken, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(revokeTimeout ?? TimeSpan.FromSeconds(5));
            return await oauth.RevokeAsync(refreshToken, timeout.Token);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private Guid TenantId => tenants.IsResolved
        ? tenants.TenantId
        : throw new InvalidOperationException("Google Health sessions require a resolved tenant context");

    public async Task<string?> AccountKeyAsync(CancellationToken ct) =>
        (await configurations.GetSecretsAsync(ConnectorName, ct)).GetValueOrDefault(AccountKey);
}

public enum GoogleHealthRevocation
{
    Revoked,
    /// <summary>Google did not confirm the revocation in time.</summary>
    Failed,
    /// <summary>Not revoked: another tenant holds a token for the same account.</summary>
    Shared,
    /// <summary>Not revoked: whether another tenant holds the account could not be told.</summary>
    Undetermined
}
