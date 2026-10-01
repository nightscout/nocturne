using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Routing.Template;

namespace Nocturne.API.Middleware;

/// <summary>
/// Serves the v1 API under <c>/api/v2</c> for every path v2 does not answer itself, as Nightscout
/// does: its v2 router mounts the whole v1 router at its root before adding the v2-only endpoints
/// (properties, authorization, ddata, notifications, summary). Clients written against Nightscout
/// rely on that, reading resources such as <c>/api/v2/entries.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Runs after <see cref="JsonExtensionMiddleware"/>, so the path it sees has already lost its
/// <c>.json</c> suffix, and before <c>UseRouting</c>, so the router matches the rewritten v1 path
/// and everything downstream treats the request as the v1 request it is being served as.
/// </para>
/// <para>
/// Which paths v2 answers itself is read from the registered route endpoints rather than listed,
/// so a v2 endpoint added later takes precedence over the fallback without a change here. Route
/// constraints are not evaluated: a path a v2 template covers stays with v2 even where a constraint
/// would reject it, which errs towards the endpoint Nocturne chose to implement.
/// </para>
/// </remarks>
/// <seealso cref="Nocturne.API.Configuration.NightscoutApiPath"/>
public class V2FallbackMiddleware
{
    private static readonly PathString V1Prefix = new("/api/v1");
    private static readonly PathString V2Prefix = new("/api/v2");

    private readonly RequestDelegate _next;
    private readonly ILogger<V2FallbackMiddleware> _logger;
    private readonly Lazy<IReadOnlyList<TemplateMatcher>> _v2Routes;

    /// <summary>
    /// Creates a new instance of <see cref="V2FallbackMiddleware"/>.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="endpoints">
    /// The application's endpoints. Read on the first request, once every endpoint is mapped.
    /// </param>
    /// <param name="logger">Logger for path-rewrite diagnostics.</param>
    public V2FallbackMiddleware(
        RequestDelegate next,
        EndpointDataSource endpoints,
        ILogger<V2FallbackMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _v2Routes = new Lazy<IReadOnlyList<TemplateMatcher>>(() => V2RouteMatchers(endpoints));
    }

    /// <summary>
    /// Rewrites a <c>/api/v2/…</c> path that no v2 endpoint matches to the same path under
    /// <c>/api/v1</c>, then continues the pipeline.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that completes when the middleware has finished processing.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (
            path.StartsWithSegments(V2Prefix, StringComparison.OrdinalIgnoreCase, out var remaining)
            && remaining.HasValue
            && remaining != "/"
            && !_v2Routes.Value.Any(route => route.TryMatch(path, new RouteValueDictionary()))
        )
        {
            var rewritten = V1Prefix.Add(remaining);
            context.Request.Path = rewritten;

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                // Strip line breaks so a path the caller controls cannot forge log lines.
                _logger.LogDebug(
                    "V2FallbackMiddleware: no v2 route for '{OriginalPath}', serving it as '{NewPath}'",
                    path.ToString().Replace("\r", "").Replace("\n", ""),
                    rewritten.ToString().Replace("\r", "").Replace("\n", "")
                );
            }
        }

        await _next(context);
    }

    private static IReadOnlyList<TemplateMatcher> V2RouteMatchers(EndpointDataSource endpoints) =>
        [.. endpoints.Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern)
            .Where(IsUnderV2)
            .Select(pattern => new TemplateMatcher(new RouteTemplate(pattern), new RouteValueDictionary()))];

    private static bool IsUnderV2(RoutePattern pattern) =>
        pattern.PathSegments.Count >= 2
        && IsLiteral(pattern.PathSegments[0], "api")
        && IsLiteral(pattern.PathSegments[1], "v2");

    private static bool IsLiteral(RoutePatternPathSegment segment, string content) =>
        segment.IsSimple
        && segment.Parts[0] is RoutePatternLiteralPart literal
        && string.Equals(literal.Content, content, StringComparison.OrdinalIgnoreCase);
}
