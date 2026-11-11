using Microsoft.AspNetCore.Cors.Infrastructure;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Multitenancy;

/// <summary>
/// Lets a documentation site hosted off the base domain (getnocturne.dev) send requests from its
/// embedded API reference to the demo tenant: the origins listed under <see cref="ConfigKey"/> may
/// call the API on the demo tenant's host, and nowhere else.
/// </summary>
/// <remarks>
/// The policy never allows credentials, so a browser refuses to send a cookie session cross-origin
/// on a preflighted request and withholds the response of any other. A page on a listed origin can
/// therefore send only a credential it holds itself, such as the demo token from
/// <c>/api/v4/demo/session</c>.
/// <para>
/// Whether a host is the demo is known only once <see cref="TenantResolutionMiddleware"/> has run,
/// so a listed origin skips the CORS branches at the head of the pipeline and is decided after it:
/// the demo tenant gets this policy, any other host the policy it would have had there. A
/// base-domain origin is never treated as listed, because it needs the credentialed default policy.
/// </para>
/// </remarks>
public static class DocsSiteCorsPolicy
{
    public const string Name = "DocsSite";
    public const string ConfigKey = "Docs:SiteOrigins";

    public static string[] Origins(IConfiguration configuration) =>
        configuration.GetSection(ConfigKey).Get<string[]>()?
            .Select(origin => origin.Trim().TrimEnd('/'))
            .Where(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && uri.AbsolutePath == "/")
            .ToArray() ?? [];

    // A predicate rather than WithOrigins: the response depends on the Origin header either way,
    // and only a predicate makes the CORS service send Vary: Origin.
    public static void Configure(CorsPolicyBuilder policy, string[] origins) =>
        policy
            .SetIsOriginAllowed(origin => origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            .AllowAnyMethod()
            .AllowAnyHeader();

    /// <summary>True when the request comes from a listed origin that is not a base-domain one.</summary>
    public static bool IsListedOrigin(
        HttpContext context, string[] origins, string baseDomain, bool allowLocalhost) =>
        origins.Length > 0
        && context.Request.Headers.Origin is [{ } origin]
        && origins.Contains(origin, StringComparer.OrdinalIgnoreCase)
        && !CorsOriginPolicy.IsAllowed(origin, baseDomain, allowLocalhost);

    /// <summary>
    /// True when the resolved tenant is the demo and the host is not one of its shares. Read after
    /// <see cref="TenantResolutionMiddleware"/>.
    /// </summary>
    public static bool AdmitsTenant(HttpContext context) =>
        context.RequestServices.GetRequiredService<ITenantAccessor>().Context is { IsDemo: true }
        && !context.IsShareAccess();
}
