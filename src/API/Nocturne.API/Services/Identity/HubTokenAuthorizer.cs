using Microsoft.EntityFrameworkCore;
using Nocturne.API.Authorization;
using Nocturne.API.Hubs;
using Nocturne.API.Middleware.Handlers;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.API.Services.Auth;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Authorizes the credentials presented in-band to a SignalR hub (the socket.io-style
/// <c>authorize</c> / <c>subscribe</c> handshake). Accepts OAuth access-token JWTs (validated
/// statelessly, tenant-pinned, then intersected with the subject's membership), legacy opaque subject
/// access tokens (hash lookup plus an explicit tenant-membership check), <c>noc_</c> direct grants
/// (grant row read on the connection's tenant, then intersected with its subject's membership), and
/// the hashed instance key.
/// </summary>
/// <seealso cref="Hubs.DataHub"/>
/// <seealso cref="Hubs.AlarmHub"/>
public interface IHubTokenAuthorizer
{
    /// <summary>
    /// Authorizes <paramref name="token"/> for the connection, returning the tenant and scopes it
    /// grants, or null when it does not authorize.
    /// </summary>
    /// <remarks>
    /// A token must be pinned to <paramref name="connectionTenantId"/> — the hub connection's tenant
    /// group is immutable, so a token from another tenant must never authorize it — and must satisfy
    /// <paramref name="requiredScope"/>. This holds on every branch: an OAuth JWT is checked against
    /// its <c>tenant</c> claim and then against a membership row on that tenant, a legacy opaque
    /// token against a membership row alone, a direct grant against its row's own <c>TenantId</c>,
    /// read on a context pinned to the connection's tenant rather than on whatever tenant state the
    /// caller's scope carries.
    /// </remarks>
    /// <param name="token">The token supplied in the hub's authorize payload.</param>
    /// <param name="connectionTenantId">The tenant resolved for the connection's HTTP handshake.</param>
    /// <param name="requiredScope">OAuth scope the token must satisfy to join the group.</param>
    Task<HubAuthorization?> AuthorizeTokenAsync(
        string token, Guid? connectionTenantId, string requiredScope);

    /// <summary>
    /// Authorizes a hashed instance key presented in-band, returning full access on the connection's
    /// tenant, or null when it does not match. The instance key is infrastructure auth (the
    /// SignalR-to-socket.io bridge), so it carries the superuser scope exactly as
    /// <see cref="Middleware.MemberScopeMiddleware"/> grants it over HTTP.
    /// </summary>
    /// <param name="presentedHash">Lowercase hex SHA-256 of the instance key, as sent by the client.</param>
    /// <param name="connectionTenantId">The tenant resolved for the connection's HTTP handshake.</param>
    HubAuthorization? AuthorizeInstanceKey(string presentedHash, Guid? connectionTenantId);
}

/// <inheritdoc cref="IHubTokenAuthorizer"/>
public class HubTokenAuthorizer : IHubTokenAuthorizer
{
    private readonly IJwtService _jwtService;
    private readonly IJwtCredentialValidator _credentialValidator;
    private readonly IAuthorizationService _authorizationService;
    private readonly ITenantMemberService _memberService;
    private readonly IDbContextFactory<NocturneDbContext> _dbContextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<HubTokenAuthorizer> _logger;

    public HubTokenAuthorizer(
        IJwtService jwtService,
        IJwtCredentialValidator credentialValidator,
        IAuthorizationService authorizationService,
        ITenantMemberService memberService,
        IDbContextFactory<NocturneDbContext> dbContextFactory,
        TimeProvider timeProvider,
        IConfiguration configuration,
        ILogger<HubTokenAuthorizer> logger)
    {
        _jwtService = jwtService;
        _credentialValidator = credentialValidator;
        _authorizationService = authorizationService;
        _memberService = memberService;
        _dbContextFactory = dbContextFactory;
        _timeProvider = timeProvider;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HubAuthorization?> AuthorizeTokenAsync(
        string token, Guid? connectionTenantId, string requiredScope)
    {
        if (connectionTenantId is null)
        {
            _logger.LogWarning("Hub token presented on a connection with no resolved tenant");
            return null;
        }

        // A direct grant is a row in oauth_grants, not a subject token, so it is decided on the
        // prefix that identifies it — the same discriminator DirectGrantTokenHandler and the
        // token-exchange endpoint use.
        if (token.StartsWith(DirectGrantTokenHandler.TokenPrefix, StringComparison.Ordinal))
        {
            return await AuthorizeDirectGrantAsync(token, connectionTenantId.Value, requiredScope);
        }

        // Once a token reads as a JWT it is decided on the JWT path only, as in
        // OAuthAccessTokenHandler.
        if (TokenFormat.IsJwt(token))
        {
            return await AuthorizeJwtAsync(token, connectionTenantId.Value, requiredScope);
        }

        return await AuthorizeOpaqueTokenAsync(token, connectionTenantId.Value, requiredScope);
    }

    /// <inheritdoc />
    public HubAuthorization? AuthorizeInstanceKey(string presentedHash, Guid? connectionTenantId)
    {
        if (connectionTenantId is null)
        {
            return null;
        }

        // Match InstanceKeyHandler's lookup: Aspire dev sets the value under
        // Parameters:instance-key (user-secrets); production sets it as the INSTANCE_KEY env var,
        // which ASP.NET Core surfaces as a top-level config key.
        var configuredKey =
            _configuration[$"Parameters:{ServiceNames.Parameters.InstanceKey}"]
            ?? _configuration[ServiceNames.ConfigKeys.InstanceKey];

        if (string.IsNullOrEmpty(configuredKey))
        {
            return null;
        }

        var expectedHash = HashUtils.Sha256Hex(configuredKey);
        if (!string.Equals(presentedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new HubAuthorization(
            connectionTenantId.Value,
            new HashSet<string> { Scope.FullAccess },
            HubCredentialKind.Infrastructure,
            SubjectId: null);
    }

    private async Task<HubAuthorization?> AuthorizeJwtAsync(
        string token, Guid connectionTenantId, string requiredScope)
    {
        var validation = await _credentialValidator.ValidateAsync(token);
        if (!validation.IsValid)
        {
            _logger.LogDebug(
                "Hub token refused ({Rejection}): {Error}", validation.Rejection, validation.Error);
            return null;
        }

        var claims = validation.Claims!;

        // Unlike the HTTP handler, an unpinned (null-tenant) JWT is rejected outright: the hub
        // group is tenant-scoped and there is no per-request middleware to re-check access.
        if (claims.TenantId != connectionTenantId)
        {
            _logger.LogWarning(
                "Hub JWT tenant mismatch: token tenant {TokenTenant}, connection tenant {ConnectionTenant}",
                claims.TenantId, connectionTenantId);
            return null;
        }

        if (claims.SubjectId == Guid.Empty)
        {
            return null;
        }

        // Membership is intersected in, exactly as it is over HTTP: MemberScopeMiddleware resolves
        // every JWT credential through the membership row and AuthenticationMiddleware rejects a
        // subject that has none. A token's scopes are frozen at issue, so without this a member
        // demoted or removed after issue keeps the access the token was minted with until it expires.
        var effectivePermissions = await _memberService.GetEffectivePermissionsAsync(
            claims.SubjectId, connectionTenantId);

        if (effectivePermissions is null)
        {
            _logger.LogWarning(
                "Hub JWT subject {SubjectId} is not a member of connection tenant {ConnectionTenant}",
                claims.SubjectId, connectionTenantId);
            return null;
        }

        var scopes = MemberScopeResolver.Resolve(
            effectivePermissions,
            AuthType.OAuthAccessToken,
            Scope.Normalize(claims.Scopes));

        if (!Scope.Satisfies(scopes, requiredScope))
        {
            return null;
        }

        // A bearer JWT always belongs to a subject: guest links are cookie-only (activation returns
        // no token), so no share-style credential reaches this path.
        return new HubAuthorization(
            connectionTenantId, scopes, HubCredentialKind.Subject, claims.SubjectId);
    }

    /// <summary>
    /// Authorizes a <c>noc_</c> direct-grant token against the connection's tenant.
    /// </summary>
    /// <remarks>
    /// The grant row is read on a context pinned to the connection's tenant and matched on that
    /// tenant explicitly, so the pin is what admits it; its subject must then hold a membership
    /// there, which bounds the grant's scopes. Those are the same two gates the credential passes
    /// over HTTP, where <see cref="DirectGrantTokenHandler"/> matches the row on the request's tenant
    /// and <see cref="Middleware.MemberScopeMiddleware"/> intersects the grant's scopes with the
    /// membership — <see cref="AuthType.DirectGrant"/> is a scoped credential, so membership cannot
    /// widen it and a demotion narrows it.
    ///
    /// It does not go through the token-exchange endpoint's direct-grant path: that reads
    /// <c>oauth_grants</c> off the scoped context, which on a hub invocation was built before any
    /// tenant was known and so matches no row.
    /// </remarks>
    private async Task<HubAuthorization?> AuthorizeDirectGrantAsync(
        string token, Guid connectionTenantId, string requiredScope)
    {
        var grant = await DirectGrantTokenHandler.FindActiveGrantAsync(
            _dbContextFactory, token, connectionTenantId, _timeProvider.GetUtcNow().UtcDateTime);

        if (grant is null)
        {
            _logger.LogDebug(
                "No active direct grant on tenant {ConnectionTenant} matches the hub token",
                connectionTenantId);
            return null;
        }

        var effectivePermissions = await _memberService.GetEffectivePermissionsAsync(
            grant.SubjectId, connectionTenantId);

        if (effectivePermissions is null)
        {
            _logger.LogWarning(
                "Hub direct grant {GrantId} belongs to subject {SubjectId}, who is not a member of connection tenant {ConnectionTenant}",
                grant.Id, grant.SubjectId, connectionTenantId);
            return null;
        }

        var scopes = MemberScopeResolver.Resolve(
            effectivePermissions, AuthType.DirectGrant, grant.Scopes.ToHashSet());

        if (!Scope.Satisfies(scopes, requiredScope))
        {
            return null;
        }

        return new HubAuthorization(
            connectionTenantId, scopes, HubCredentialKind.Subject, grant.SubjectId);
    }

    /// <summary>
    /// Authorizes a legacy opaque subject access token. The exchange only proves the token exists, so
    /// the resulting identity is then pinned to the connection's tenant by an explicit
    /// tenant-membership row, which does not rely on the database's tenant scoping being in place.
    /// </summary>
    private async Task<HubAuthorization?> AuthorizeOpaqueTokenAsync(
        string token, Guid connectionTenantId, string requiredScope)
    {
        var authResponse = await _authorizationService.GenerateJwtFromAccessTokenAsync(token);
        if (authResponse?.Token is null)
        {
            return null;
        }

        // The exchange mints a JWT for the resolved identity; re-reading it is how the subject id
        // comes back out.
        var validation = _jwtService.ValidateAccessToken(authResponse.Token);
        if (!validation.IsValid || validation.Claims is null)
        {
            _logger.LogDebug(
                "Hub opaque token exchange produced an unusable JWT: {Error}", validation.Error);
            return null;
        }

        var claims = validation.Claims;

        // A subject-token exchange mints an unpinned JWT; only the direct-grant exchange pins a
        // tenant, and direct grants are routed away from this path. A pinned JWT here therefore
        // resolved something this path does not decide, and is refused rather than guessed at.
        if (claims.TenantId is not null)
        {
            _logger.LogWarning(
                "Hub opaque token exchange returned a tenant-pinned JWT on the subject-token path");
            return null;
        }

        if (claims.SubjectId == Guid.Empty)
        {
            return null;
        }

        // Legacy subject tokens carry no tenant pin, so membership is the pin.
        var effectivePermissions = await _memberService.GetEffectivePermissionsAsync(
            claims.SubjectId, connectionTenantId);

        if (effectivePermissions is null)
        {
            _logger.LogWarning(
                "Hub opaque token subject {SubjectId} is not a member of connection tenant {ConnectionTenant}",
                claims.SubjectId, connectionTenantId);
            return null;
        }

        // AuthType.LegacyAccessToken is in MemberScopeResolver.UnscopedCredentialTypes, so membership
        // is the whole authority and the credential's own scope list is not consulted at all —
        // matching what MemberScopeMiddleware resolves for the same credential presented over HTTP.
        var memberScopes = MemberScopeResolver.Resolve(
            effectivePermissions,
            AuthType.LegacyAccessToken,
            credentialScopes: new HashSet<string>());

        return Scope.Satisfies(memberScopes, requiredScope)
            ? new HubAuthorization(
                connectionTenantId, memberScopes, HubCredentialKind.Subject, claims.SubjectId)
            : null;
    }
}
