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
/// <para>
/// Pipeline order (position 3 of 8 custom middleware):
/// <see cref="JsonExtensionMiddleware"/>,
/// <b>ApexCallbackRelayMiddleware</b>, <see cref="Multitenancy.TenantResolutionMiddleware"/>,
/// <see cref="TenantSetupMiddleware"/>, <see cref="AuthenticationMiddleware"/>,
/// <see cref="MemberScopeMiddleware"/>.
/// </para>
/// <para>
/// The slug is read out of the protected <c>state</c> query parameter by the flow that issued it
/// (<see cref="IOidcAuthService.TryReadTenantSlug"/>,
/// <see cref="GoogleHealthOAuthState.TryReadTenantSlug"/>), never from a caller-supplied
/// parameter, so the redirect target is one this instance issued.
/// </para>
/// </remarks>
/// <seealso cref="Multitenancy.TenantResolutionMiddleware"/>
/// <seealso cref="BaseDomainOptions"/>
public partial class ApexCallbackRelayMiddleware(
    RequestDelegate next,
    ILogger<ApexCallbackRelayMiddleware> logger,
    IOptions<BaseDomainOptions> config)
{
    private static readonly Dictionary<string, Func<IServiceProvider, string, string?>> Callbacks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["/api/auth/oidc/callback"] = ReadOidcSlug,
            ["/api/auth/oidc/link/callback"] = ReadOidcSlug,
            [GoogleHealthPlatformOptions.CallbackPath] = (services, state) =>
                services.GetRequiredService<GoogleHealthOAuthState>().TryReadTenantSlug(state),
        };

    private readonly BaseDomainOptions _config = config.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        var redirectUrl = string.IsNullOrEmpty(_config.BaseDomain) ||
                          !Callbacks.TryGetValue(context.Request.Path.Value ?? "", out var readSlug)
            ? null
            : TenantRedirect(context, readSlug);

        if (redirectUrl is null)
        {
            await next(context);
            return;
        }

        logger.LogInformation("Relaying apex callback {Path} to its tenant subdomain", context.Request.Path);
        context.Response.Redirect(redirectUrl);
    }

    private string? TenantRedirect(HttpContext context, Func<IServiceProvider, string, string?> readSlug)
    {
        var baseDomainHost = _config.BaseDomain.Split(':')[0];
        if (context.Request.Host.Host.EndsWith($".{baseDomainHost}", StringComparison.OrdinalIgnoreCase))
            return null;

        var state = context.Request.Query["state"].FirstOrDefault();
        if (string.IsNullOrEmpty(state))
            return null;

        var tenantSlug = readSlug(context.RequestServices, state);
        if (string.IsNullOrEmpty(tenantSlug) || !SlugPattern().IsMatch(tenantSlug))
            return null;

        // The tenant host answers on whatever port the browser reached the apex on.
        var tenantHost = context.Request.Host.Port is { } port
            ? new HostString($"{tenantSlug}.{baseDomainHost}", port)
            : new HostString($"{tenantSlug}.{baseDomainHost}");
        return $"{context.Request.PublicScheme()}://{tenantHost}{context.Request.Path}{context.Request.QueryString}";
    }

    private static string? ReadOidcSlug(IServiceProvider services, string state) =>
        services.GetRequiredService<IOidcAuthService>().TryReadTenantSlug(state);

    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{0,61}[a-z0-9]$")]
    private static partial System.Text.RegularExpressions.Regex SlugPattern();
}
