using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Core.Contracts.Auth;

namespace Nocturne.API.Middleware;

/// <summary>
/// Relays OAuth callbacks that land on the apex domain to the tenant subdomain the sign-in
/// started on. A provider can register only one redirect URI, so it points at the apex; running
/// before <see cref="Multitenancy.TenantResolutionMiddleware"/> lets the callback be processed
/// on the tenant host, where its session cookies are.
/// </summary>
/// <remarks>
/// The slug is read out of the protected <c>state</c> query parameter by the flow that issued it
/// (<see cref="IOidcAuthService.TryReadTenantSlug"/>,
/// <see cref="GoogleHealthOAuthState.TryReadTenantSlug"/>), never from a caller-supplied
/// parameter, so the redirect target is one this instance issued.
/// </remarks>
/// <seealso cref="Multitenancy.TenantResolutionMiddleware"/>
/// <seealso cref="BaseDomainOptions"/>
public partial class ApexCallbackRelayMiddleware(
    RequestDelegate next,
    ILogger<ApexCallbackRelayMiddleware> logger,
    IOptions<BaseDomainOptions> config)
{
    private static readonly Dictionary<string, Relay> Callbacks = new Relay[]
    {
        new("/api/auth/oidc/callback", ReadOidcSlug),
        new("/api/auth/oidc/link/callback", ReadOidcSlug),
        new(GoogleHealthPlatformOptions.CallbackPath, (services, state) =>
            services.GetRequiredService<GoogleHealthOAuthState>().TryReadTenantSlug(state)),
    }.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);

    private readonly string? _baseHost = config.Value.SplitHostPort().Host;

    public async Task InvokeAsync(HttpContext context)
    {
        if (_baseHost is null ||
            !Callbacks.TryGetValue(context.Request.Path.Value ?? "", out var relay) ||
            TenantRedirect(context, relay) is not { } redirectUrl)
        {
            await next(context);
            return;
        }

        logger.LogInformation("Relaying apex callback {Path} to its tenant subdomain", relay.Path);
        context.Response.Redirect(redirectUrl);
    }

    /// <remarks>
    /// Only the slug and the port come from the request, and each is validated into a form that
    /// cannot leave this deployment: the slug against <see cref="SlugPattern"/> as a label under the
    /// configured base host, the port as a number. The port is the request's rather than the
    /// configured one because a TLS proxy can front the apex on a port of its own (the e2e stack).
    /// The query is forwarded after a literal <c>?</c>, so it cannot reach the authority.
    /// </remarks>
    private string? TenantRedirect(HttpContext context, Relay relay)
    {
        if (context.Request.Host.Host.EndsWith($".{_baseHost}", StringComparison.OrdinalIgnoreCase))
            return null;

        var state = context.Request.Query["state"].FirstOrDefault();
        if (string.IsNullOrEmpty(state))
            return null;

        var tenantSlug = relay.ReadSlug(context.RequestServices, state);
        if (string.IsNullOrEmpty(tenantSlug) || !SlugPattern().IsMatch(tenantSlug))
            return null;

        var port = context.Request.Host.Port ?? -1;
        if (port is not (-1 or (>= 1 and <= 65535)))
            return null;

        var tenantOrigin = new UriBuilder(context.Request.PublicScheme(), $"{tenantSlug}.{_baseHost}", port);
        return Uri.TryCreate(tenantOrigin.ToString(), UriKind.Absolute, out var origin)
            ? $"{origin.GetLeftPart(UriPartial.Authority)}{relay.Path}?{context.Request.QueryString.Value![1..]}"
            : null;
    }

    private static string? ReadOidcSlug(IServiceProvider services, string state) =>
        services.GetRequiredService<IOidcAuthService>().TryReadTenantSlug(state);

    private sealed record Relay(string Path, Func<IServiceProvider, string, string?> ReadSlug);

    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{0,61}[a-z0-9]$")]
    private static partial System.Text.RegularExpressions.Regex SlugPattern();
}
