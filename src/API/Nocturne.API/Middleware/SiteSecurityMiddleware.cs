using Nocturne.API.Extensions;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Middleware;

/// <summary>
/// Middleware that enforces site-wide authentication requirements when configured.
/// When site lockdown is enabled, unauthenticated requests to protected routes
/// will be denied with a 401 Unauthorized response.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline order (position 7 of 7 custom middleware -- last before ASP.NET authorization):
/// <see cref="JsonExtensionMiddleware"/>,
/// <see cref="OidcCallbackRedirectMiddleware"/>, <see cref="Multitenancy.TenantResolutionMiddleware"/>,
/// <see cref="TenantSetupMiddleware"/>, <see cref="AuthenticationMiddleware"/>,
/// <see cref="MemberScopeMiddleware"/>, <b>SiteSecurityMiddleware</b>.
/// </para>
/// <para>
/// Reads the <see cref="AuthContext"/> populated by <see cref="AuthenticationMiddleware"/>
/// via <see cref="Extensions.HttpContextExtensions.GetAuthContext"/>. Controlled by the
/// <c>Security:RequireAuthentication</c> configuration key.
/// </para>
/// </remarks>
/// <seealso cref="AuthenticationMiddleware"/>
/// <seealso cref="MemberScopeMiddleware"/>
public class SiteSecurityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SiteSecurityMiddleware> _logger;
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Creates a new instance of <see cref="SiteSecurityMiddleware"/>.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">Logger for site lockdown diagnostics.</param>
    /// <param name="configuration">Application configuration for reading <c>Security:RequireAuthentication</c>.</param>
    public SiteSecurityMiddleware(
        RequestDelegate next,
        ILogger<SiteSecurityMiddleware> logger,
        IConfiguration configuration
    )
    {
        _next = next;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Enforces site-wide authentication when lockdown is enabled.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that completes when the middleware has finished processing.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        // Check if authentication is required for the site
        var authEnabled = _configuration.GetValue<bool>("Security:RequireAuthentication", false);

        if (!authEnabled)
        {
            // Site is open, no lockdown - proceed normally
            await _next(context);
            return;
        }

        // Site is locked down - check if the route should be protected
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

        // Allow certain routes without authentication even in lockdown mode
        if (IsPublicRoute(path))
        {
            await _next(context);
            return;
        }

        // Check if user is authenticated
        var authContext = context.GetAuthContext();
        if (authContext == null || !authContext.IsAuthenticated)
        {
            _logger.LogDebug(
                "Site lockdown active: Denying unauthenticated request to {Path}",
                path
            );

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "authentication_required",
                error_description = "This site requires authentication. Please log in to access this resource.",
            });
            return;
        }

        // User is authenticated, proceed
        await _next(context);
    }

    /// <summary>
    /// Determine if a route should be publicly accessible even when lockdown is enabled.
    /// </summary>
    /// <param name="path">The lowercased request path to evaluate.</param>
    /// <returns><see langword="true"/> if the route is always public (auth, health, docs, assets); otherwise <see langword="false"/>.</returns>
    private static bool IsPublicRoute(string path)
    {
        // Authentication and authorization endpoints must remain accessible
        if (path.StartsWith("/api/v4/auth/") ||
            path.StartsWith("/api/auth/oidc/") ||
            path.StartsWith("/api/oauth/") ||
            path.StartsWith("/api/v4/oauth/") ||
            path.StartsWith("/api/v4/local/"))
        {
            return true;
        }

        // Well-known discovery endpoints (OIDC, OAuth metadata)
        if (path.StartsWith("/.well-known/"))
        {
            return true;
        }

        // Health check and status endpoints for monitoring
        if (path.StartsWith("/health") ||
            path == "/" ||
            path == "/alive" ||
            path == "/ready")
        {
            return true;
        }

        // OpenAPI/Swagger documentation
        if (path.StartsWith("/openapi") ||
            path.StartsWith("/scalar") ||
            path.StartsWith("/swagger"))
        {
            return true;
        }

        // Static assets and frontend files
        if (path.StartsWith("/_app") ||
            path.StartsWith("/assets") ||
            path.StartsWith("/favicon"))
        {
            return true;
        }

        // All other routes require authentication when lockdown is enabled
        return false;
    }
}
