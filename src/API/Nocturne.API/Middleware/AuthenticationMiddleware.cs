using Nocturne.API.Middleware.Handlers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using OAuthScopes = Nocturne.Core.Models.Authorization.OAuthScopes;
using ScopeTranslator = Nocturne.Core.Models.Authorization.ScopeTranslator;

namespace Nocturne.API.Middleware;

/// <summary>
/// Middleware for handling authentication through a chain of handlers.
/// Handlers are executed in priority order (lowest first).
/// The first handler to return success or failure stops the chain.
/// </summary>
public class AuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuthenticationMiddleware> _logger;
    private readonly IAuthHandler[] _handlers;

    /// <summary>
    /// Creates a new instance of AuthenticationMiddleware
    /// </summary>
    public AuthenticationMiddleware(
        RequestDelegate next,
        ILogger<AuthenticationMiddleware> logger,
        IEnumerable<IAuthHandler> handlers
    )
    {
        _next = next;
        _logger = logger;

        // Sort handlers by priority (lowest first)
        _handlers = handlers.OrderBy(h => h.Priority).ToArray();
    }

    /// <summary>
    /// Process the HTTP request through the authentication pipeline
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            var authContext = await AuthenticateRequestAsync(context);

            // Set authentication context in HttpContext items
            context.Items["AuthContext"] = authContext;

            // Build and set permission trie for fast permission checking
            var permissionTrie = new PermissionTrie();
            if (authContext.IsAuthenticated && authContext.Permissions.Count > 0)
            {
                permissionTrie.Add(authContext.Permissions);
            }
            context.Items["PermissionTrie"] = permissionTrie;

            // Resolve OAuth scopes from either explicit scopes (OAuth tokens) or
            // translated from legacy permissions (api-secret, access tokens, etc.)
            IReadOnlySet<string> grantedScopes;
            if (authContext.IsAuthenticated && authContext.Scopes.Count > 0)
            {
                // OAuth token path: scopes came directly from the token claims
                grantedScopes = OAuthScopes.Normalize(authContext.Scopes);
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
            context.Items["GrantedScopes"] = grantedScopes;

            // Also set the legacy AuthenticationContext for backward compatibility
            context.Items["AuthenticationContext"] = MapToLegacyContext(authContext);

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

                foreach (var permission in authContext.Permissions)
                {
                    claims.Add(new("permission", permission));
                }

                var identity = new System.Security.Claims.ClaimsIdentity(claims, "Nocturne");
                context.User = new System.Security.Claims.ClaimsPrincipal(identity);

            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during authentication");
            SetUnauthenticated(context);
        }

        await _next(context);
    }

    /// <summary>
    /// Run through the handler chain to authenticate the request
    /// </summary>
    private async Task<AuthContext> AuthenticateRequestAsync(HttpContext context)
    {
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

        return AuthContext.Unauthenticated();
    }

    /// <summary>
    /// Set unauthenticated context on the HttpContext
    /// </summary>
    private static void SetUnauthenticated(HttpContext context)
    {
        var authContext = AuthContext.Unauthenticated();
        context.Items["AuthContext"] = authContext;
        context.Items["PermissionTrie"] = new PermissionTrie();
        context.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>();
        context.Items["AuthenticationContext"] = MapToLegacyContext(authContext);
    }

    /// <summary>
    /// Map new AuthContext to legacy AuthenticationContext for backward compatibility
    /// </summary>
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
    /// Map new AuthType to legacy AuthenticationType enum
    /// </summary>
    private static AuthenticationType MapAuthType(AuthType authType)
    {
        return authType switch
        {
            AuthType.None => AuthenticationType.None,
            AuthType.ApiSecret => AuthenticationType.ApiSecret,
            AuthType.LegacyJwt => AuthenticationType.JwtToken,
            AuthType.LegacyAccessToken => AuthenticationType.JwtToken,
            AuthType.OidcToken => AuthenticationType.JwtToken,
            AuthType.SessionCookie => AuthenticationType.JwtToken,
            AuthType.OAuthAccessToken => AuthenticationType.JwtToken,
            _ => AuthenticationType.None,
        };
    }
}

/// <summary>
/// Legacy authentication context for backward compatibility.
/// New code should use AuthContext from Core.Models.Authorization.
/// </summary>
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
