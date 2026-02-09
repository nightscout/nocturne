using Nocturne.API.Extensions;

namespace Nocturne.API.Middleware;

/// <summary>
/// Middleware that enforces site-wide authentication requirements when configured.
/// When site lockdown is enabled, unauthenticated requests to protected routes
/// will be denied with a 401 Unauthorized response.
/// </summary>
public class SiteSecurityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SiteSecurityMiddleware> _logger;
    private readonly IConfiguration _configuration;

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
    /// Determine if a route should be publicly accessible even when lockdown is enabled
    /// </summary>
    private static bool IsPublicRoute(string path)
    {
        // Authentication and authorization endpoints must remain accessible
        if (path.StartsWith("/api/v4/auth/") ||
            path.StartsWith("/api/v4/oidc/") ||
            path.StartsWith("/api/v4/oauth/") ||
            path.StartsWith("/api/v4/local/"))
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
