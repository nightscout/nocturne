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
/// resolved tenant. Only reachable over the internal compose network from Caddy;
/// it reveals nothing a visitor couldn't learn by loading the public subdomain.
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
