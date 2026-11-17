namespace Nocturne.API.Extensions;

/// <summary>
/// Response headers every API reply carries, whichever edge fronts it: the web app's /api proxy
/// relays them, and a deployment that routes to the API directly (the Helm ingress) gets them too.
/// </summary>
/// <remarks>
/// HSTS is not here: it belongs to the hop that terminates TLS (the bundled Caddy, or the
/// operator's own proxy), and the API only ever speaks plain HTTP.
/// </remarks>
public static class SecurityHeadersExtensions
{
    /// <summary>
    /// Stamps the headers as the response starts, so an error response the exception handler
    /// rewrites carries them too. A <c>Referrer-Policy</c> an endpoint chose is kept.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var response = context.Response;
            response.OnStarting(() =>
            {
                response.Headers.XContentTypeOptions = "nosniff";
                response.Headers.XFrameOptions = "DENY";
                response.Headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
                return Task.CompletedTask;
            });
            return next(context);
        });
}
