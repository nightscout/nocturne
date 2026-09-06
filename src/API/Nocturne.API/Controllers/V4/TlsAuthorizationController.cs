using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// On-demand TLS authorization for the bundled Caddy reverse proxy.
/// Caddy calls this before requesting a certificate for a hostname (its
/// <c>on_demand_tls.ask</c> endpoint) so that certificates are only issued for
/// the apex domain, real tenant subdomains, and live public share hosts —
/// preventing unbounded issuance for arbitrary hostnames pointed at the server.
/// </summary>
/// <remarks>
/// Anonymous and tenantless by design: it lives under the <c>/api/v4/platform/</c>
/// prefix, which <see cref="TenantResolutionMiddleware"/> allows through without a
/// resolved tenant.
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
    private readonly IShareTokenResolver _shareTokens;
    private readonly BaseDomainOptions _baseDomain;

    public TlsAuthorizationController(
        ITenantService tenantService,
        IShareTokenResolver shareTokens,
        IOptions<BaseDomainOptions> baseDomain)
    {
        _tenantService = tenantService;
        _shareTokens = shareTokens;
        _baseDomain = baseDomain.Value;
    }

    /// <summary>
    /// Returns 200 when a certificate should be issued for <paramref name="domain"/>
    /// (the apex domain, an active tenant subdomain, or a share host whose token is live),
    /// 404 otherwise.
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

        var slug = SubdomainParser.Extract(host, baseDomain);
        if (slug is null)
            return NotFound();

        // Public share host — {token}.share.{baseDomain}, one label deeper than a tenant.
        // Resolving the token rather than accepting any well-formed share host is what stops a
        // stranger with DNS pointed here from driving issuance for names of their choosing; a
        // caller must already hold the token to get a 200, and the token is the secret the link
        // hands out anyway. Resolved without recording an access: asking whether to mint a
        // certificate is not someone opening the link.
        if (SubdomainParser.TryExtractShareToken(slug, out var shareToken))
        {
            var shareTenant = await _shareTokens.ResolveWithoutRecordingAccessAsync(shareToken, ct);
            return shareTenant is { IsActive: true } ? Ok() : NotFound();
        }

        // Tenant subdomain — only issue for an existing, active tenant. Match slugs the same
        // way TenantResolutionMiddleware does (ordinal,
        // case-sensitive) so we never authorize a cert for a host that would
        // then fail to resolve to a tenant.
        var tenants = await _tenantService.GetAllAsync(ct);
        var isActiveTenant = tenants.Any(t =>
            string.Equals(t.Slug, slug, StringComparison.Ordinal) && t.IsActive);

        return isActiveTenant ? Ok() : NotFound();
    }
}
