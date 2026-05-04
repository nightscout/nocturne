using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Nocturne.API.Middleware;

/// <summary>
/// Middleware to handle <c>.json</c> extensions in API routes for legacy Nightscout compatibility.
/// Strips <c>.json</c> extensions from paths so they can be handled by standard controller routes.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline order (position 1 of 7 custom middleware -- runs first, before routing):
/// <b>JsonExtensionMiddleware</b>,
/// <see cref="OidcCallbackRedirectMiddleware"/>, <see cref="Multitenancy.TenantResolutionMiddleware"/>,
/// <see cref="TenantSetupMiddleware"/>, <see cref="AuthenticationMiddleware"/>,
/// <see cref="MemberScopeMiddleware"/>, <see cref="SiteSecurityMiddleware"/>.
/// </para>
/// <para>
/// This middleware rewrites the request path before <c>UseRouting</c> so that the router
/// matches the rewritten path. Also ensures the <c>Accept</c> header includes
/// <c>application/json</c>.
/// </para>
/// </remarks>
/// <seealso cref="Attributes.NightscoutEndpointAttribute"/>
public class JsonExtensionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<JsonExtensionMiddleware> _logger;

    /// <summary>
    /// Creates a new instance of <see cref="JsonExtensionMiddleware"/>.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">Logger for path-rewrite diagnostics.</param>
    public JsonExtensionMiddleware(RequestDelegate next, ILogger<JsonExtensionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Strips the <c>.json</c> extension from the request path if present and ensures the
    /// <c>Accept</c> header includes <c>application/json</c>.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that completes when the middleware has finished processing.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;

        // Check if the path ends with .json (skip /openapi paths — MapOpenApi expects .json)
        if (
            !string.IsNullOrEmpty(path)
            && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase)
        )
        {
            // Remove the .json extension
            var newPath = path.Substring(0, path.Length - 5);
            context.Request.Path = new PathString(newPath);

            // Ensure the Accept header includes application/json
            if (
                !context.Request.Headers.ContainsKey("Accept")
                || !context.Request.Headers["Accept"].ToString().Contains("application/json")
            )
            {
                context.Request.Headers["Accept"] = "application/json";
            }

            // Log the path rewrite for debugging
            _logger.LogDebug(
                "JsonExtensionMiddleware: Rewrote '{OriginalPath}' to '{NewPath}'",
                path,
                newPath
            );
        }

        await _next(context);
    }
}
