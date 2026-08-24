using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Hubs;

/// <summary>
/// Subject-scoped SignalR hub for the cross-tenant caregiver overview: one connection receives
/// an <c>overviewUpdate</c> ping for every glucose-readable tenant of the authorized subject.
///
/// Unlike <see cref="TenantAwareHub"/> descendants this hub has no tenant at handshake — it is
/// reached from the apex (tenantless) and derives its group set from the subject's memberships
/// in <see cref="Authorize"/>, joining <c>{tenantId}:overview</c> per qualifying tenant. The
/// group set is server-derived and never client-supplied.
///
/// Group membership is lost on reconnect; clients must re-invoke <see cref="Authorize"/> after
/// every (re)connection.
///
/// Group membership persists for the connection lifetime: a membership revoked mid-connection
/// keeps receiving <c>overviewUpdate</c> pings (the payload carries only the tenant id; the
/// overview refetch re-checks authorization) until the connection ends.
///
/// Legacy opaque subject tokens are not accepted in the Authorize payload, but they authenticate
/// via the upgrade request's AuthContext like any authenticated request — they identify a subject
/// rather than a tenant, and membership bounds what they can see, matching the REST overview
/// endpoint.
///
/// <c>overviewUpdate</c> fires on every data-update broadcast (treatments, devicestatus, etc.),
/// not only glucose entries; consumers should debounce refetches.
/// </summary>
// Connections authenticate in-band after negotiate (session cookie from the upgrade request or
// a JWT in the Authorize payload), so the HTTP fallback authorization policy must not gate the
// handshake.
[AllowAnonymous]
public class OverviewHub : Hub
{
    /// <summary>Group name suffix; full group is <c>{tenantId}:overview</c>.</summary>
    public const string GroupName = "overview";

    private readonly ILogger<OverviewHub> _logger;
    private readonly ITenantOverviewService _overviewService;
    private readonly IJwtService _jwtService;
    private readonly IOAuthTokenRevocationCache _revocationCache;

    public OverviewHub(
        ILogger<OverviewHub> logger,
        ITenantOverviewService overviewService,
        IJwtService jwtService,
        IOAuthTokenRevocationCache revocationCache)
    {
        _logger = logger;
        _overviewService = overviewService;
        _jwtService = jwtService;
        _revocationCache = revocationCache;
    }

    /// <summary>
    /// Authorizes the connection as a subject and joins the <c>{tenantId}:overview</c> group of
    /// every tenant the subject may read glucose for (resolved via
    /// <see cref="ITenantOverviewService.GetGlucoseReadTenantsAsync"/>). Accepts the upgrade
    /// request's authenticated subject credential (AuthContext) or a subject-scoped OAuth
    /// access-token JWT in <paramref name="request"/>. Opaque tokens in the payload are rejected
    /// because this hub implements no in-band verification path for them; presented on the upgrade
    /// request they authenticate normally. An empty tenant list is a valid success.
    /// </summary>
    [HubAuthenticationMethod]
    [HubTenantGroup]
    public async Task<OverviewAuthorizeResponse> Authorize(OverviewAuthorizeRequest request)
    {
        try
        {
            Guid subjectId;
            IReadOnlySet<string> tokenScopes;
            AuthType authType;

            var httpContext = Context.GetHttpContext();
            var authContext = httpContext?.Items["AuthContext"] as AuthContext;

            if (authContext is { IsAuthenticated: true, SubjectId: not null })
            {
                // The hub is reachable on tenant subdomains as well as the apex, so the upgrade
                // request may carry a credential that only ever authorized one tenant: a
                // tenant-pinned OAuth token, a noc_ direct grant, an api-secret, a guest code, a
                // platform-access grant. Joining the subject's every glucose-readable tenant would
                // widen such a credential past the tenant it was issued for, so only credentials
                // that are the human's own login are accepted here — the same set
                // MemberScopeResolver treats as carrying no grant of their own, because "presents
                // no scope ceiling" and "is not a per-tenant grant" are the same property. A type
                // absent from the set is rejected, so a new credential type fails closed.
                if (!MemberScopeResolver.UnscopedCredentialTypes.Contains(authContext.AuthType))
                {
                    return OverviewAuthorizeResponse.Failed(
                        "This credential is bound to a single tenant; the overview hub requires a subject-scoped credential.");
                }

                subjectId = authContext.SubjectId.Value;
                authType = authContext.AuthType;
                tokenScopes = httpContext!.GetGrantedScopes();
            }
            else if (!string.IsNullOrEmpty(request.Token))
            {
                // JWTs are three-segment; legacy opaque tokens are not (mirrors HubTokenAuthorizer).
                if (request.Token.Count(c => c == '.') != 2)
                {
                    return OverviewAuthorizeResponse.Failed(
                        "Opaque access tokens cannot be authenticated in-band here; present one on "
                        + "the connection request, or send an OAuth JWT.");
                }

                var validation = _jwtService.ValidateAccessToken(request.Token);
                if (!validation.IsValid || validation.Claims is null)
                {
                    _logger.LogDebug("Overview hub JWT validation failed: {Error}", validation.Error);
                    return OverviewAuthorizeResponse.Failed("Invalid token.");
                }

                var claims = validation.Claims;
                if (!string.IsNullOrEmpty(claims.JwtId)
                    && await _revocationCache.IsRevokedAsync(claims.JwtId))
                {
                    _logger.LogDebug("Overview hub JWT has been revoked (jti: {Jti})", claims.JwtId);
                    return OverviewAuthorizeResponse.Failed("Invalid token.");
                }

                // A tenant-pinned JWT is only valid on the tenant that issued it
                // (HubTokenAuthorizer enforces the pin strictly); accepting it here would
                // widen its audience to all of the subject's tenants.
                if (claims.TenantId is not null)
                {
                    return OverviewAuthorizeResponse.Failed(
                        "Tenant-pinned tokens are not accepted here; the overview hub requires a subject-scoped token.");
                }

                if (claims.SubjectId == Guid.Empty)
                {
                    return OverviewAuthorizeResponse.Failed("Token carries no subject.");
                }

                subjectId = claims.SubjectId;
                tokenScopes = Scope.Normalize(claims.Scopes);
                authType = AuthType.OAuthAccessToken;
            }
            else
            {
                return OverviewAuthorizeResponse.Failed("Not authenticated.");
            }

            var tenants = await _overviewService.GetGlucoseReadTenantsAsync(
                subjectId, tokenScopes, authType, Context.ConnectionAborted);

            var tenantIds = new List<Guid>(tenants.Count);
            foreach (var (tenant, _) in tenants)
            {
                await Groups.AddToGroupAsync(
                    Context.ConnectionId,
                    TenantAwareHub.FormatTenantGroup(tenant.Id.ToString(), GroupName));
                tenantIds.Add(tenant.Id);
            }

            _logger.LogInformation(
                "Client {ConnectionId} authorized for overview of {TenantCount} tenants",
                Context.ConnectionId, tenantIds.Count);

            return new OverviewAuthorizeResponse { Success = true, TenantIds = tenantIds };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Error during overview authorization for client {ConnectionId}",
                Context.ConnectionId);
            return OverviewAuthorizeResponse.Failed("Authorization failed.");
        }
    }
}

/// <summary>Authorization request for <see cref="OverviewHub.Authorize"/>.</summary>
public class OverviewAuthorizeRequest
{
    /// <summary>OAuth access-token JWT; omit when the connection carries a session cookie.</summary>
    public string? Token { get; set; }
}

/// <summary>Result of <see cref="OverviewHub.Authorize"/>.</summary>
public class OverviewAuthorizeResponse
{
    public bool Success { get; set; }

    /// <summary>The tenant ids the connection is now subscribed to.</summary>
    public List<Guid> TenantIds { get; set; } = new();

    public string? Error { get; set; }

    public static OverviewAuthorizeResponse Failed(string error) =>
        new() { Success = false, Error = error };
}
