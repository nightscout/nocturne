using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Nocturne.API.Middleware;

/// <summary>
/// Handles <c>.json</c>, <c>.txt</c>, <c>.csv</c> and <c>.tsv</c> extensions in API routes for legacy
/// Nightscout compatibility. Strips the extension from the path so a standard controller route
/// handles it, and replaces the caller's <c>Accept</c> header with the extension's media type.
/// </summary>
/// <remarks>
/// Must run before <c>UseRouting</c>, so the router matches the stripped path.
/// </remarks>
/// <seealso cref="Attributes.NightscoutEndpointAttribute"/>
public class JsonExtensionMiddleware
{
    private const string JsonMediaType = "application/json";

    /// <summary>
    /// Prefixes whose <c>.json</c> suffix is part of the real route rather than a legacy
    /// Nightscout content-negotiation hint: the OpenAPI documents served by <c>MapOpenApi</c>,
    /// and the OIDC key set that both discovery documents advertise as <c>jwks_uri</c>.
    /// </summary>
    private static readonly string[] LiteralJsonPrefixes = ["/openapi", "/.well-known"];

    /// <summary>
    /// Prefixes whose text extensions Nightscout honours: the entries router, which also serves
    /// <c>times</c> and <c>slice</c>.
    /// </summary>
    private static readonly string[] TextExtensionPrefixes =
        ["/api/v1/entries", "/api/v1/times/", "/api/v1/slice/"];

    private static readonly Dictionary<string, string> MediaTypeByExtension = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        [".json"] = JsonMediaType,
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".tsv"] = "text/tab-separated-values",
    };

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
    /// Strips a recognised extension from the request path and sets the <c>Accept</c> header to
    /// its media type.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that completes when the middleware has finished processing.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;

        if (
            !string.IsNullOrEmpty(path)
            && path.LastIndexOf('.') is var dot
            && dot > path.LastIndexOf('/')
            && MediaTypeByExtension.TryGetValue(path[dot..], out var mediaType)
            && Honours(path, mediaType)
        )
        {
            var newPath = path[..dot];
            context.Request.Path = new PathString(newPath);

            context.Request.Headers.Accept = mediaType;

            _logger.LogDebug(
                "JsonExtensionMiddleware: Rewrote '{OriginalPath}' to '{NewPath}'",
                path,
                newPath
            );
        }

        await _next(context);
    }

    private static bool Honours(string path, string mediaType) =>
        mediaType == JsonMediaType
            ? !StartsWithAny(path, LiteralJsonPrefixes)
            : StartsWithAny(path, TextExtensionPrefixes);

    private static bool StartsWithAny(string path, string[] prefixes) =>
        prefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
