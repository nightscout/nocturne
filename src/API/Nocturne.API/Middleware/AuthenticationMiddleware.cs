using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nocturne.API.Authorization;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Scope = Nocturne.Core.Models.Authorization.Scope;
using ScopeTranslator = Nocturne.Core.Models.Authorization.ScopeTranslator;
using Nocturne.API.Extensions;

namespace Nocturne.API.Middleware;

/// <summary>
/// Middleware for handling authentication through a chain of <see cref="IAuthHandler"/> implementations.
/// Handlers are executed in priority order (lowest first).
/// The first handler to return success or failure stops the chain.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline order (position 5 of 7 custom middleware):
/// <see cref="JsonExtensionMiddleware"/>,
/// <see cref="OidcCallbackRedirectMiddleware"/>, <see cref="Multitenancy.TenantResolutionMiddleware"/>,
/// <see cref="TenantSetupMiddleware"/>, <b>AuthenticationMiddleware</b>,
/// <see cref="MemberScopeMiddleware"/>, <see cref="SiteSecurityMiddleware"/>.
/// </para>
/// <para>
/// Populates <c>HttpContext.Items[AuthContextKeys.AuthContext]</c> with an <see cref="AuthContext"/>,
/// <c>HttpContext.Items[AuthContextKeys.PermissionTrie]</c> with a <see cref="PermissionTrie"/>,
/// and <c>HttpContext.Items[AuthContextKeys.GrantedScopes]</c> with normalized OAuth scopes.
/// Depends on <see cref="Multitenancy.TenantResolutionMiddleware"/> having resolved a
/// <see cref="TenantContext"/> first. For unauthenticated requests with a resolved tenant,
/// delegates to <see cref="PublicAccessCacheService"/> for public/read-only access.
/// </para>
/// </remarks>
/// <seealso cref="IAuthHandler"/>
/// <seealso cref="MemberScopeMiddleware"/>
/// <seealso cref="SiteSecurityMiddleware"/>
/// <seealso cref="Multitenancy.TenantResolutionMiddleware"/>
public class AuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuthenticationMiddleware> _logger;
    private readonly IAuthHandler[] _handlers;
    private readonly bool _isDevelopment;
    private readonly PublicAccessCacheService _publicAccessCacheService;
    private readonly string _accessTokenCookieName;
    private readonly string _refreshTokenCookieName;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Creates a new instance of <see cref="AuthenticationMiddleware"/>.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">Logger for authentication diagnostics.</param>
    /// <param name="handlers">All registered <see cref="IAuthHandler"/> implementations, sorted by priority internally.</param>
    /// <param name="environment">Host environment used to enable development-mode auto-authentication.</param>
    /// <param name="publicAccessCacheService">Cache for resolving public (unauthenticated) access permissions per tenant.</param>
    /// <param name="oidcOptions">OIDC configuration providing cookie names for session detection.</param>
    /// <param name="scopeFactory">Factory for creating scoped services outside the request scope.</param>
    public AuthenticationMiddleware(
        RequestDelegate next,
        ILogger<AuthenticationMiddleware> logger,
        IEnumerable<IAuthHandler> handlers,
        IHostEnvironment environment,
        PublicAccessCacheService publicAccessCacheService,
        IOptions<OidcOptions> oidcOptions,
        IServiceScopeFactory scopeFactory
    )
    {
        _next = next;
        _logger = logger;
        _isDevelopment = environment.IsDevelopment();
        _publicAccessCacheService = publicAccessCacheService;
        _accessTokenCookieName = oidcOptions.Value.Cookie.AccessTokenName;
        _refreshTokenCookieName = oidcOptions.Value.Cookie.RefreshTokenName;
        _scopeFactory = scopeFactory;

        // Sort handlers by priority (lowest first)
        _handlers = handlers.OrderBy(h => h.Priority).ToArray();
    }

    /// <summary>
    /// Process the HTTP request through the authentication pipeline.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that completes when the middleware has finished processing.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            var authContext = await AuthenticateRequestAsync(context);

            // Set authentication context in HttpContext items
            context.SetAuthContext(authContext);

            // Set tenant ID from the resolved tenant context
            if (context.GetTenantContext() is { } tenantCtx)
            {
                authContext.TenantId = tenantCtx.TenantId;
            }

            // Build and set permission trie for fast permission checking
            var permissionTrie = new PermissionTrie();
            if (authContext.IsAuthenticated && authContext.Permissions.Count > 0)
            {
                permissionTrie.Add(authContext.Permissions);
            }
            context.SetPermissionTrie(permissionTrie);

            // Resolve OAuth scopes from either explicit scopes (OAuth tokens) or
            // translated from legacy permissions (api-secret, access tokens, etc.)
            IReadOnlySet<string> grantedScopes;
            if (authContext.IsAuthenticated && authContext.Scopes.Count > 0)
            {
                // OAuth token path: scopes came directly from the token claims
                grantedScopes = Scope.Normalize(authContext.Scopes);
            }
            else if (authContext.IsAuthenticated && authContext.Permissions.Count > 0)
            {
                // Legacy path: translate Shiro-style permissions to scopes
                grantedScopes = ScopeTranslator.FromPermissions(authContext.Permissions);
            }
            else
            {
                grantedScopes = new HashSet<string>();
            }
            context.SetGrantedScopes(grantedScopes);

            // Also set the legacy AuthenticationContext for backward compatibility
            context.SetLegacyAuthContext(MapToLegacyContext(authContext));

            // Load platform admin flag from subject before building claims,
            // so [Authorize(Roles = "platform_admin")] works correctly.
            if (authContext is { IsAuthenticated: true, SubjectId: not null })
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<Nocturne.Infrastructure.Data.NocturneDbContext>();
                var isPlatformAdmin = await db.Subjects
                    .Where(s => s.Id == authContext.SubjectId.Value)
                    .OrderBy(s => s.Id)
                    .Select(s => s.IsPlatformAdmin)
                    .FirstOrDefaultAsync();
                authContext.IsPlatformAdmin = isPlatformAdmin;
            }

            // Set HttpContext.User for [Authorize] attribute to work
            if (authContext.IsAuthenticated)
            {
                var claims = new List<System.Security.Claims.Claim>
                {
                    new(System.Security.Claims.ClaimTypes.NameIdentifier, authContext.SubjectId?.ToString() ?? ""),
                    new(System.Security.Claims.ClaimTypes.Name, authContext.SubjectName ?? ""),
                };

                if (!string.IsNullOrEmpty(authContext.Email))
                {
                    claims.Add(new(System.Security.Claims.ClaimTypes.Email, authContext.Email));
                }

                foreach (var role in authContext.Roles)
                {
                    claims.Add(new(System.Security.Claims.ClaimTypes.Role, role));
                }

                if (authContext.IsPlatformAdmin)
                {
                    claims.Add(new(System.Security.Claims.ClaimTypes.Role, "platform_admin"));
                }

                foreach (var permission in authContext.Permissions)
                {
                    claims.Add(new("permission", permission));
                }

                var identity = new System.Security.Claims.ClaimsIdentity(claims, "Nocturne");
                context.User = new System.Security.Claims.ClaimsPrincipal(identity);

            }
            else
            {
                // This middleware owns the final principal on EVERY path, including rejection.
                // The framework's authentication middleware runs ahead of this one (minimal hosting
                // auto-inserts it at the head of the pipeline because AddAuthentication is
                // registered), so by the time we get here context.User may already hold the
                // JwtBearer scheme's principal — built with no issuer or audience check, no tenant
                // pin and no revocation check. Without this else, a credential the handler chain
                // REJECTED keeps that principal: [Authorize] reads the principal, not Items, so a
                // revoked grant, a token pinned to another tenant, or a credential presented on a
                // share host would still reach every bare-[Authorize] controller — including the
                // sensor-glucose read. The membership check below cannot catch it either, since it
                // only runs for IsAuthenticated: true.
                SetUnauthenticated(context);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during authentication");
            SetUnauthenticated(context);
        }

        // Verify authenticated subject is a member of the resolved tenant
        var resolvedAuth = context.GetAuthContext();
        if (resolvedAuth is { IsAuthenticated: true, SubjectId: not null, TenantId: not null })
        {
            // Skip membership check for ApiSecret and InstanceKey auth (grants admin on the resolved
            // tenant), and for PlatformAccess grants (PlatformAccessCookieHandler already proved the
            // grant is platform-access-marked and pinned to this tenant).
            if (resolvedAuth.AuthType is not (AuthType.ApiKey or AuthType.InstanceKey or AuthType.PlatformAccess))
            {
                var tenantMemberService = context.RequestServices.GetRequiredService<ITenantMemberService>();
                var isMember = await tenantMemberService.IsMemberAsync(
                    resolvedAuth.SubjectId!.Value,
                    resolvedAuth.TenantId!.Value);

                if (!isMember)
                {
                    // An invite-token-authorized endpoint is how a non-member joins, so reducing
                    // the request to anonymous there leaves the accept path reachable only by
                    // people who are already members. Identity only, and only when the route's
                    // token is a live invite of this tenant.
                    if (!await TryKeepIdentityForInviteAsync(context, resolvedAuth))
                    {
                        _logger.LogWarning(
                            "Subject {SubjectId} is not a member of tenant {TenantId}",
                            resolvedAuth.SubjectId, resolvedAuth.TenantId);
                        SetUnauthenticated(context);
                    }
                }
            }
        }

        // Public read access is granted only when the request arrived via a valid share token
        // ({token}.share.{baseDomain}); TenantResolutionMiddleware sets ShareAccess. The bare
        // {slug}.{baseDomain} host is login-only — an unauthenticated request there gets nothing,
        // even when the tenant's Public subject carries a read role.
        resolvedAuth = context.GetAuthContext();
        if (resolvedAuth is { IsAuthenticated: false }
            && context.IsShareAccess()
            && context.GetTenantContext() is { } publicTenantCtx)
        {
            var publicAccess = await _publicAccessCacheService.GetPublicAccessAsync(publicTenantCtx.TenantId);
            if (publicAccess != null)
            {
                var publicAuthContext = new AuthContext
                {
                    IsAuthenticated = false,
                    AuthType = AuthType.None,
                    SubjectId = publicAccess.SubjectId,
                    TenantId = publicTenantCtx.TenantId,
                    LimitTo24Hours = publicAccess.LimitTo24Hours,
                };
                context.SetAuthContext(publicAuthContext);

                // The Public subject's effective permissions are stored in the OAuth scope
                // vocabulary (glucose.read, ...) — the same vocabulary member grants use — so
                // normalize them the way MemberScopeMiddleware does for members; the
                // FromPermissions union also accepts legacy api:* trie strings on old rows.
                // Then narrow to the shareable read scopes: the share host can never resolve
                // to more than public read access, so a broader grant on the Public membership
                // (readwrite, superuser) degrades to its read counterpart via SatisfiesScope.
                var resolvedGrants = Scope.Normalize(publicAccess.EffectivePermissions)
                    .Union(ScopeTranslator.FromPermissions(publicAccess.EffectivePermissions))
                    .ToHashSet();
                var publicScopes = Scope.PublicShareScopes
                    .Where(scope => Scope.Satisfies(resolvedGrants, scope))
                    .ToHashSet();
                context.SetGrantedScopes((IReadOnlySet<string>)publicScopes);

                // Legacy (HasPermissions-gated) endpoints check the trie, so derive it from the
                // narrowed scopes; a share that resolves to zero scopes gets an empty trie and
                // is rejected by the policy instead of passing authorization and reading nothing.
                // The scope atoms are added alongside so a share whose categories ToPermissions has
                // no legacy api:* string for still carries a non-empty trie. Every scope in
                // PublicShareScopes currently maps, so this is redundant today and guards the case
                // where a new shareable category is added without a legacy equivalent.
                var publicPermissionTrie = new PermissionTrie();
                publicPermissionTrie.Add(ScopeTranslator.ToPermissions(publicScopes));
                publicPermissionTrie.Add(publicScopes);
                context.SetPermissionTrie(publicPermissionTrie);

                // Carry the share's visible categories and history window to the DbContext
                // factory for the share RLS policies. Resolved here (post-auth); a share whose
                // CSV is never set is denied all categorized data by the policy (fail-closed).
                var categoryReadContext = context.RequestServices.GetService<ICategoryReadContext>();
                categoryReadContext?.SetVisibleCategories(ShareDataCategories.ComputeVisibleCategoriesCsv(publicScopes));
                categoryReadContext?.SetFullHistory(!publicAccess.LimitTo24Hours);

                context.SetLegacyAuthContext(MapToLegacyContext(publicAuthContext));

                _logger.LogDebug(
                    "Public access resolved for tenant {TenantId} with {Count} permissions",
                    publicTenantCtx.TenantId, publicAccess.EffectivePermissions.Count);
            }
        }

        await _next(context);
    }

    /// <summary>
    /// Run through the <see cref="IAuthHandler"/> chain to authenticate the request.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>An <see cref="AuthContext"/> representing the authentication result.</returns>
    private async Task<AuthContext> AuthenticateRequestAsync(HttpContext context)
    {
        // Public share host ({token}.share.{baseDomain}): never honor credentials. The share host
        // serves only the anonymous read-only view, so a logged-in owner's session cookie must not
        // authenticate the request — the host can never resolve to more than public read access.
        if (context.IsShareAccess())
        {
            return AuthContext.Unauthenticated();
        }

        foreach (var handler in _handlers)
        {
            try
            {
                var result = await handler.AuthenticateAsync(context);

                if (result.Succeeded)
                {

                    return result.AuthContext!;
                }

                if (!result.ShouldSkip)
                {
                    // Handler recognized credentials but they were invalid
                    _logger.LogDebug(
                        "Authentication failed by {Handler}: {Error}",
                        handler.Name,
                        result.Error
                    );

                    // Return unauthenticated context but don't try other handlers
                    return AuthContext.Unauthenticated();
                }

                // Handler skipped - try next handler
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Handler {Handler} threw an exception", handler.Name);
                // Continue to next handler
            }
        }

        // In development mode, auto-authenticate as admin when a session cookie is present
        // but no handler succeeded (e.g., expired token without refresh).
        // When no session cookie is present, fall through to public access or unauthenticated.
        if (_isDevelopment)
        {
            var hasSessionCookie =
                context.Request.Cookies.ContainsKey(_accessTokenCookieName) ||
                context.Request.Cookies.ContainsKey(_refreshTokenCookieName);

            if (hasSessionCookie)
            {
                _logger.LogDebug("Development mode: auto-authenticating as admin (session cookie present)");
                return new AuthContext
                {
                    IsAuthenticated = true,
                    AuthType = AuthType.ApiKey,
                    SubjectName = "dev-admin",
                    Permissions = ["*"],
                    Roles = ["admin", "platform_admin"],
                    IsPlatformAdmin = true,
                };
            }
        }

        return AuthContext.Unauthenticated();
    }

    /// <summary>
    /// For a subject who authenticated but is not a member of the resolved tenant, keep the
    /// identity — and only the identity — when the request targets an endpoint marked
    /// <see cref="InviteTokenAuthorizedAttribute"/> and its <c>{token}</c> route value names a
    /// currently valid invite of that same tenant.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="resolvedAuth">The authenticated context that failed the membership check.</param>
    /// <returns><c>true</c> when the identity was kept; <c>false</c> to reject as usual.</returns>
    /// <remarks>
    /// The invite token is the whole of the authorization here, so it is validated before anything
    /// is kept: the lookup is bounded by the resolved tenant, and the invite must not be expired,
    /// revoked or exhausted. What survives is a subject id and a display name — no permissions, no
    /// roles, no scopes and an empty <see cref="PermissionTrie"/> — so every gated endpoint still
    /// refuses the caller, and the marked endpoints authorize on the invite itself.
    /// </remarks>
    private async Task<bool> TryKeepIdentityForInviteAsync(HttpContext context, AuthContext resolvedAuth)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<InviteTokenAuthorizedAttribute>() == null)
            return false;

        if (!context.Request.RouteValues.TryGetValue(
                InviteTokenAuthorizedAttribute.TokenRouteValue, out var routeValue)
            || routeValue is not string token
            || string.IsNullOrEmpty(token))
        {
            return false;
        }

        var inviteService = context.RequestServices.GetRequiredService<IMemberInviteService>();
        var invite = await inviteService.GetInviteByTokenAsync(token, resolvedAuth.TenantId!.Value);
        if (invite is not { IsValid: true })
            return false;

        var identityOnly = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = resolvedAuth.AuthType,
            SubjectId = resolvedAuth.SubjectId,
            TenantId = resolvedAuth.TenantId,
            SubjectName = resolvedAuth.SubjectName,
            Email = resolvedAuth.Email,
        };

        context.SetAuthContext(identityOnly);
        context.SetPermissionTrie(new PermissionTrie());
        context.SetGrantedScopes((IReadOnlySet<string>)new HashSet<string>());
        context.SetLegacyAuthContext(MapToLegacyContext(identityOnly));

        // The principal built earlier carries the subject's roles, its platform-admin role and a
        // claim per permission. [Authorize] reads the principal, so it is replaced rather than
        // reused: the marked endpoints need only to know who is asking.
        context.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim(
                        System.Security.Claims.ClaimTypes.NameIdentifier,
                        identityOnly.SubjectId?.ToString() ?? ""),
                    new System.Security.Claims.Claim(
                        System.Security.Claims.ClaimTypes.Name, identityOnly.SubjectName ?? ""),
                ],
                "NocturneInvite"));

        _logger.LogInformation(
            "MemberInviteAudit: {Event} invite_id={InviteId} tenant_id={TenantId} subject_id={SubjectId}",
            "invite_identity_kept", invite.Id, resolvedAuth.TenantId, resolvedAuth.SubjectId);

        return true;
    }

    /// <summary>
    /// Set unauthenticated <see cref="AuthContext"/> on the <see cref="HttpContext"/>,
    /// clearing the <see cref="PermissionTrie"/> and granted scopes.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    private static void SetUnauthenticated(HttpContext context)
    {
        var authContext = AuthContext.Unauthenticated();
        context.SetAuthContext(authContext);
        context.SetPermissionTrie(new PermissionTrie());
        context.SetGrantedScopes((IReadOnlySet<string>)new HashSet<string>());
        context.SetLegacyAuthContext(MapToLegacyContext(authContext));

        // Clearing Items is not enough: this method is also the tenant-membership rejection
        // path, and by then the principal above has already been built. [Authorize] reads
        // HttpContext.User, not Items, so leaving a populated principal here authenticates a
        // rejected caller against any endpoint whose only gate is [Authorize].
        context.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity());
    }

    /// <summary>
    /// Map new <see cref="AuthContext"/> to legacy <see cref="AuthenticationContext"/> for backward compatibility.
    /// </summary>
    /// <param name="authContext">The modern authentication context to convert.</param>
    /// <returns>A legacy <see cref="AuthenticationContext"/> for v1/v2/v3 API consumers.</returns>
    private static AuthenticationContext MapToLegacyContext(AuthContext authContext)
    {
        return new AuthenticationContext
        {
            IsAuthenticated = authContext.IsAuthenticated,
            AuthenticationType = MapAuthType(authContext.AuthType),
            SubjectId = authContext.SubjectId?.ToString() ?? authContext.SubjectName,
            Permissions = authContext.Permissions,
            Token = authContext.RawToken,
        };
    }

    /// <summary>
    /// Map new <see cref="AuthType"/> to legacy <see cref="AuthenticationType"/> enum.
    /// </summary>
    /// <param name="authType">The modern auth type to convert.</param>
    /// <returns>The corresponding legacy <see cref="AuthenticationType"/> value.</returns>
    private static AuthenticationType MapAuthType(AuthType authType)
    {
        return authType switch
        {
            AuthType.None => AuthenticationType.None,
            AuthType.ApiKey => AuthenticationType.ApiSecret,
            AuthType.InstanceKey => AuthenticationType.ApiSecret,
            AuthType.LegacyJwt => AuthenticationType.JwtToken,
            AuthType.LegacyAccessToken => AuthenticationType.JwtToken,
            AuthType.OidcToken => AuthenticationType.JwtToken,
            AuthType.SessionCookie => AuthenticationType.JwtToken,
            _ => AuthenticationType.None,
        };
    }
}

/// <summary>
/// Legacy authentication context for backward compatibility.
/// New code should use <see cref="AuthContext"/> from <c>Core.Models.Authorization</c>.
/// </summary>
/// <seealso cref="AuthContext"/>
public class AuthenticationContext
{
    /// <summary>
    /// Whether the request is authenticated
    /// </summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// Type of authentication used
    /// </summary>
    public AuthenticationType AuthenticationType { get; set; }

    /// <summary>
    /// Subject identifier (user/device ID)
    /// </summary>
    public string? SubjectId { get; set; }

    /// <summary>
    /// List of permissions for this authentication
    /// </summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>
    /// JWT token if using token authentication
    /// </summary>
    public string? Token { get; set; }
}

/// <summary>
/// Legacy authentication types for backward compatibility
/// </summary>
public enum AuthenticationType
{
    /// <summary>
    /// No authentication
    /// </summary>
    None,

    /// <summary>
    /// API secret authentication
    /// </summary>
    ApiSecret,

    /// <summary>
    /// JWT token authentication
    /// </summary>
    JwtToken,
}
