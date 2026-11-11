using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Nocturne.API.Multitenancy;

/// <summary>
/// Lets a documentation site hosted off the base domain (getnocturne.dev) send requests from its
/// embedded API reference: the origins listed under <see cref="ConfigKey"/> may call the API.
/// </summary>
/// <remarks>
/// The policy never allows credentials, so a browser refuses to send a cookie session cross-origin
/// on a preflighted request and withholds the response of any other. A page on a listed origin can
/// therefore send only a credential it holds itself, such as the demo token from
/// <c>/api/v4/demo/session</c>, which any non-browser client can already send. Empty by default, so
/// an instance that lists no origin behaves exactly as before.
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

    public static void Configure(CorsPolicyBuilder policy, string[] origins) =>
        policy
            .WithOrigins(origins)
            .AllowAnyMethod()
            .AllowAnyHeader();

    public static bool Applies(HttpContext context, string[] origins) =>
        origins.Length > 0
        && context.Request.Headers.Origin is [{ } origin]
        && origins.Contains(origin, StringComparer.OrdinalIgnoreCase);
}
