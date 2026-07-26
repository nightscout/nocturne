using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// On-demand TLS authorization for the bundled Caddy reverse proxy.
/// Caddy calls this before requesting a certificate for a hostname (its
/// <c>on_demand_tls.ask</c> endpoint) so that certificates are only issued for
/// the apex domain and real tenant subdomains — preventing unbounded issuance
/// for arbitrary hostnames pointed at the server.
/// </summary>
/// <remarks>
/// Anonymous and tenantless by design: it lives under the <c>/api/v4/platform/</c>
/// prefix, which <see cref="TenantResolutionMiddleware"/> allows through without a
/// resolved tenant, and <see cref="Middleware.SiteSecurityMiddleware"/> allowlists it
/// under lockdown (matched exactly, so sibling paths are not allowlisted).
/// <para>
/// It stays anonymous because Caddy's <c>on_demand_tls.ask</c> takes only a URL and sends no
/// custom headers, so there is no credential to require. It is not authenticated-by-network
/// either: the API is only <c>expose</c>d on the compose network, but the edge route
/// <c>/api/{**catch-all}</c> reaches nocturne-web, whose <c>proxyHandle</c> forwards all of
/// <c>/api</c> to this API. That path is what makes the 200-vs-404 answer an anonymous
/// tenant-slug oracle, so <c>proxyHandle</c> refuses this specific path
/// (<c>INTERNAL_ONLY_API_PATHS</c>); Caddy calls the API container directly and is unaffected.
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("api/v4/platform/tls-authorize")]
[Produces("application/json")]
public class TlsAuthorizationController : ControllerBase
{
    private readonly ITenantService _tenantService;
    private readonly BaseDomainOptions _baseDomain;

    public TlsAuthorizationController(
        ITenantService tenantService,
        IOptions<BaseDomainOptions> baseDomain)
    {
        _tenantService = tenantService;
        _baseDomain = baseDomain.Value;
    }

    /// <summary>
    /// Returns 200 when a certificate should be issued for <paramref name="domain"/>
    /// (the apex domain or an active tenant subdomain), 404 otherwise.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Authorize([FromQuery] string? domain, CancellationToken ct)
    {
        var baseDomain = _baseDomain.BaseDomain;
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(baseDomain))
            return NotFound();

        var host = domain.Split(':')[0];
        var baseHost = baseDomain.Split(':')[0];

        // Apex domain — single-tenant deployments serve here (HTTP-01, not
        // on-demand — but authorize it anyway for completeness).
        if (string.Equals(host, baseHost, StringComparison.OrdinalIgnoreCase))
            return Ok();

        // Tenant subdomain — only issue for an existing, active tenant.
        var slug = SubdomainParser.Extract(host, baseDomain);
        if (slug is null)
            return NotFound();

        // Match slugs the same way TenantResolutionMiddleware does (ordinal,
        // case-sensitive) so we never authorize a cert for a host that would
        // then fail to resolve to a tenant.
        var tenants = await _tenantService.GetAllAsync(ct);
        var isActiveTenant = tenants.Any(t =>
            string.Equals(t.Slug, slug, StringComparison.Ordinal) && t.IsActive);

        return isActiveTenant ? Ok() : NotFound();
    }
}
