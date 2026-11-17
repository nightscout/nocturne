using Microsoft.AspNetCore.Cors.Infrastructure;
using Nocturne.API.Services.Platform;

namespace Nocturne.API.Multitenancy;

/// <summary>
/// Nightscout's <c>cors</c> feature: with <see cref="EnabledFeatures.Cors"/> in the instance's enable
/// list, the legacy API (v1, v2, v3 and <c>/api/versions</c>) answers any origin, so browser apps on
/// other domains such as Nightscout Reporter can read it with a token, an <c>Authorization</c> header
/// or an <c>api-secret</c> header.
/// </summary>
/// <remarks>
/// The policy never allows credentials, so a cookie session is never usable cross-origin. A base-domain
/// origin stays on the credentialed default policy (<see cref="CorsOriginPolicy"/>) on these paths too,
/// because the web app's own cookie-authenticated calls need it. A share host follows the instance
/// switch: its reads are already anonymous and category-gated, and the policy exposes nothing a
/// non-browser client cannot already fetch.
/// </remarks>
public static class LegacyApiCorsPolicy
{
    public const string Name = "LegacyApi";

    private static readonly PathString[] LegacyPaths =
    [
        "/api/v1",
        "/api/v2",
        "/api/v3",
        "/api/versions",
        "/pebble",
    ];

    public static void Configure(CorsPolicyBuilder policy) =>
        policy
            .AllowAnyOrigin()
            .WithMethods("GET", "PUT", "POST", "DELETE", "OPTIONS")
            // NIGHTSCOUT-COMPAT: upstream omits api-secret, so a cross-origin api-secret header fails
            // its preflight. Nocturne documents api-secret as the legacy auth header, so it is allowed.
            .WithHeaders("Content-Type", "Authorization", "Content-Length", "X-Requested-With", "api-secret");

    /// <summary>
    /// True when the request is a legacy API call from an origin outside the base domain on an instance
    /// that enables <c>cors</c>.
    /// </summary>
    public static bool Applies(
        HttpContext context, IConfiguration configuration, string baseDomain, bool allowLocalhost) =>
        LegacyPaths.Any(path => context.Request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase))
        && !CorsOriginPolicy.IsAllowed(context.Request.Headers.Origin, baseDomain, allowLocalhost)
        && EnabledFeatures.IsEnabled(configuration, EnabledFeatures.Cors);
}
