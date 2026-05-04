using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;

namespace Nocturne.API.Middleware;

/// <summary>
/// Redirects OIDC callbacks that land on the apex domain to the originating
/// tenant subdomain. Runs before <see cref="Multitenancy.TenantResolutionMiddleware"/> so
/// cookies set on the tenant subdomain are available when the callback is
/// actually processed.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline order (position 3 of 8 custom middleware):
/// <see cref="JsonExtensionMiddleware"/>,
/// <b>OidcCallbackRedirectMiddleware</b>, <see cref="Multitenancy.TenantResolutionMiddleware"/>,
/// <see cref="TenantSetupMiddleware"/>, <see cref="AuthenticationMiddleware"/>,
/// <see cref="MemberScopeMiddleware"/>, <see cref="SiteSecurityMiddleware"/>.
/// </para>
/// <para>
/// Extracts the tenant slug from the base64-encoded OIDC <c>state</c> query parameter
/// and issues a 302 redirect to the correct <c>{slug}.{baseDomain}</c> URL.
/// </para>
/// </remarks>
/// <seealso cref="Multitenancy.TenantResolutionMiddleware"/>
/// <seealso cref="MultitenancyConfiguration"/>
public partial class OidcCallbackRedirectMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<OidcCallbackRedirectMiddleware> _logger;
    private readonly MultitenancyConfiguration _config;

    private static readonly string[] CallbackPaths =
    [
        "/api/auth/oidc/callback",
        "/api/auth/oidc/link/callback",
    ];

    /// <summary>
    /// Creates a new instance of <see cref="OidcCallbackRedirectMiddleware"/>.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">Logger for redirect diagnostics.</param>
    /// <param name="config">Multitenancy configuration providing the base domain.</param>
    public OidcCallbackRedirectMiddleware(
        RequestDelegate next,
        ILogger<OidcCallbackRedirectMiddleware> logger,
        IOptions<MultitenancyConfiguration> config)
    {
        _next = next;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>
    /// Checks if the request is an OIDC callback on the apex domain and redirects to the
    /// originating tenant subdomain if so.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A task that completes when the middleware has finished processing.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsOidcCallbackPath(context.Request.Path) || string.IsNullOrEmpty(_config.BaseDomain))
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
