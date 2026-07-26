using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Authorizes in-band tokens presented to SignalR hubs (the socket.io-style <c>authorize</c>
/// handshake). Accepts both OAuth access-token JWTs (validated statelessly, tenant-pinned,
/// scope-checked) and legacy opaque subject access tokens (hash lookup).
/// </summary>
/// <seealso cref="Hubs.DataHub"/>
public interface IHubTokenAuthorizer
{
    /// <summary>
    /// Whether <paramref name="token"/> authorizes the connection. OAuth JWTs must be valid,
    /// unrevoked, pinned to <paramref name="connectionTenantId"/> (the hub connection's tenant
    /// group is immutable, so a token from another tenant must never authorize it), and satisfy
    /// <paramref name="requiredScope"/>. Non-JWT tokens fall back to the legacy opaque
    /// access-token lookup.
    /// </summary>
    /// <param name="token">The token supplied in the hub's authorize payload.</param>
    /// <param name="connectionTenantId">The tenant resolved for the connection's HTTP handshake.</param>
    /// <param name="requiredScope">OAuth scope a JWT must satisfy to join the group.</param>
    Task<bool> IsTokenAuthorizedAsync(string token, Guid? connectionTenantId, string requiredScope);
}

/// <inheritdoc cref="IHubTokenAuthorizer"/>
public class HubTokenAuthorizer : IHubTokenAuthorizer
{
    private readonly IJwtService _jwtService;
    private readonly IOAuthTokenRevocationCache _revocationCache;
    private readonly IOAuthGrantService _grantService;
    private readonly IAuthorizationService _authorizationService;
    private readonly ILogger<HubTokenAuthorizer> _logger;

    public HubTokenAuthorizer(
        IJwtService jwtService,
        IOAuthTokenRevocationCache revocationCache,
        IOAuthGrantService grantService,
        IAuthorizationService authorizationService,
        ILogger<HubTokenAuthorizer> logger)
    {
        _jwtService = jwtService;
        _revocationCache = revocationCache;
        _grantService = grantService;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    public async Task<bool> IsTokenAuthorizedAsync(
        string token, Guid? connectionTenantId, string requiredScope)
    {
        // JWTs are three-segment; legacy opaque tokens are not. Mirrors OAuthAccessTokenHandler:
        // once a token reads as a JWT it is decided on the JWT path only.
        if (token.Count(c => c == '.') == 2)
        {
            return await IsJwtAuthorizedAsync(token, connectionTenantId, requiredScope);
        }

        var authResponse = await _authorizationService.GenerateJwtFromAccessTokenAsync(token);
        return authResponse != null;
    }

    private async Task<bool> IsJwtAuthorizedAsync(
        string token, Guid? connectionTenantId, string requiredScope)
    {
        var validation = _jwtService.ValidateAccessToken(token);
        if (!validation.IsValid || validation.Claims is null)
        {
            _logger.LogDebug("Hub JWT validation failed: {Error}", validation.Error);
            return false;
        }

        var claims = validation.Claims;

        // Unlike the HTTP handler, an unpinned (null-tenant) JWT is rejected outright: the hub
        // group is tenant-scoped and there is no per-request middleware to re-check access.
        if (connectionTenantId is null || claims.TenantId != connectionTenantId)
        {
            _logger.LogWarning(
                "Hub JWT tenant mismatch: token tenant {TokenTenant}, connection tenant {ConnectionTenant}",
                claims.TenantId, connectionTenantId);
            return false;
        }

        if (!string.IsNullOrEmpty(claims.JwtId)
            && await _revocationCache.IsRevokedAsync(claims.JwtId))
        {
            _logger.LogDebug("Hub JWT has been revoked (jti: {Jti})", claims.JwtId);
            return false;
        }

        // A grant revocation reaches outstanding access tokens through the grant id they carry.
        if (claims.GrantId.HasValue
            && await _grantService.IsGrantRevokedAsync(claims.GrantId.Value, connectionTenantId.Value))
        {
            _logger.LogDebug("Hub JWT's grant has been revoked (grant: {GrantId})", claims.GrantId);
            return false;
        }

        return OAuthScopes.SatisfiesScope(claims.Scopes, requiredScope);
    }
}
