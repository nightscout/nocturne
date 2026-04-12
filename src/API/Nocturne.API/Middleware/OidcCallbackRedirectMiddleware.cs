using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;

namespace Nocturne.API.Middleware;

/// <summary>
/// Redirects OIDC callbacks that land on the apex domain to the originating
/// tenant subdomain. Runs before <see cref="TenantResolutionMiddleware"/> so
/// cookies set on the tenant subdomain are available when the callback is
/// actually processed.
/// </summary>
public partial class OidcCallbackRedirectMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<OidcCallbackRedirectMiddleware> _logger;
    private readonly MultitenancyConfiguration _config;

    private static readonly string[] CallbackPaths =
    [
        "/api/v4/oidc/callback",
        "/api/v4/oidc/link/callback",
    ];

    public OidcCallbackRedirectMiddleware(
        RequestDelegate next,
        ILogger<OidcCallbackRedirectMiddleware> logger,
        IOptions<MultitenancyConfiguration> config)
    {
        _next = next;
        _logger = logger;
        _config = config.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (string.IsNullOrEmpty(_config.BaseDomain) || !IsOidcCallbackPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var host = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(':')[0]
                   ?? context.Request.Host.Host;
        var baseDomainHost = _config.BaseDomain.Split(':')[0];

        // If there's already a subdomain, pass through — the callback is on the right host.
        if (host.EndsWith($".{baseDomainHost}", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Apex domain — try to extract TenantSlug from the state query parameter.
        var stateParam = context.Request.Query["state"].FirstOrDefault();
        if (string.IsNullOrEmpty(stateParam))
        {
            await _next(context);
            return;
        }

        var tenantSlug = ExtractTenantSlug(stateParam);
        if (string.IsNullOrEmpty(tenantSlug))
        {
            await _next(context);
            return;
        }

        if (!SlugPattern().IsMatch(tenantSlug))
        {
            await _next(context);
            return;
        }

        var scheme = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()
                     ?? context.Request.Scheme;
        var redirectUrl = $"{scheme}://{tenantSlug}.{baseDomainHost}{context.Request.Path}{context.Request.QueryString}";

        _logger.LogInformation(
            "Redirecting OIDC callback from apex to tenant subdomain {TenantSlug}",
            tenantSlug);

        context.Response.Redirect(redirectUrl);
    }

    private static bool IsOidcCallbackPath(PathString path)
    {
        var value = path.Value ?? "";
        return CallbackPaths.Any(p => value.Equals(p, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractTenantSlug(string encoded)
    {
        try
        {
            // Restore base64 padding
            var padded = (encoded.Length % 4) switch
            {
                2 => encoded + "==",
                3 => encoded + "=",
                _ => encoded,
            };

            var bytes = Convert.FromBase64String(padded.Replace("-", "+").Replace("_", "/"));
            var json = Encoding.UTF8.GetString(bytes);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("TenantSlug", out var prop))
                return prop.GetString();

            return null;
        }
        catch
        {
            return null;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{0,61}[a-z0-9]$")]
    private static partial System.Text.RegularExpressions.Regex SlugPattern();
}
