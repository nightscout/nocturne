namespace Nocturne.API.Middleware;

/// <summary>
/// Rejects or redirects HTTP requests to HTTPS. Runs first in the pipeline
/// to prevent WebAuthn failures and setup state corruption from insecure access.
/// </summary>
/// <remarks>
/// <para>
/// GET/HEAD over HTTP receive a 301 redirect to the HTTPS equivalent.
/// Other methods receive 400 Bad Request (redirecting a POST would lose the body).
/// </para>
/// <para>
/// Behind a reverse proxy (YARP, Caddy, nginx), the request reaches the API
/// as plain HTTP with <c>X-Forwarded-Proto: https</c>. The middleware treats
/// this as secure.
/// </para>
/// <para>
/// Health-check paths (<c>/health</c>, <c>/alive</c>) are exempt so
/// orchestrators can probe over HTTP.
/// </para>
/// <para>
/// Opt-out: set <c>Security:AllowHttp = true</c> in configuration.
/// </para>
/// </remarks>
public class HttpsRequirementMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<HttpsRequirementMiddleware> _logger;
    private readonly bool _allowHttp;

    private static readonly string[] BypassPaths = ["/health", "/alive"];

    public HttpsRequirementMiddleware(
        RequestDelegate next,
        ILogger<HttpsRequirementMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _allowHttp = configuration.GetValue<bool>("Security:AllowHttp", false);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_allowHttp)
        {
            await _next(context);
            return;
        }

        if (IsSecure(context.Request))
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";
        if (BypassPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
        {
            var httpsUrl = $"https://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}";
            _logger.LogWarning("Redirecting HTTP {Method} {Path} to HTTPS", method, path);
            context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
            context.Response.Headers.Location = httpsUrl;
            return;
        }

        _logger.LogWarning("Rejecting HTTP {Method} {Path} — HTTPS is required", method, path);
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = "https_required",
            message = "HTTPS is required. Please access this site using https://.",
        });
    }

    private static bool IsSecure(HttpRequest request)
    {
        if (request.IsHttps)
            return true;

        // Behind a reverse proxy, the original scheme is in X-Forwarded-Proto
        var forwardedProto = request.Headers["X-Forwarded-Proto"].FirstOrDefault();
        return string.Equals(forwardedProto, "https", StringComparison.OrdinalIgnoreCase);
    }
}
