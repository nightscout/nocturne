using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Controllers.V4.PlatformAdmin;

/// <summary>
/// Mints a short-lived, tenant-pinned platform-admin access grant so a platform admin can enter a
/// tenant they are not a member of. The grant is a JWT carrying a <c>platform_access</c> marker,
/// stored in a dedicated <c>.basedomain</c> cookie and consumed by
/// <see cref="Middleware.Handlers.PlatformAccessCookieHandler"/> on the target subdomain.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is anonymous so it can bounce an unauthenticated operator through OIDC login and
/// back. It authorizes via the operator's existing nocturne session (the <c>platform_admin</c>
/// flag): a session is required, and it must be a platform admin. The grant — and therefore all
/// resulting audit attribution — is minted for the operator's real subject.
/// </para>
/// <para>
/// Reached at the apex (the operator is not yet on any tenant subdomain); the target tenant is
/// taken from the <c>tenant</c> (slug) query parameter, not from subdomain resolution. The path is
/// registered as tenantless-allowed in <see cref="TenantResolutionMiddleware"/>.
/// </para>
/// <para>
/// Like <c>GET /api/auth/oidc/login</c>, this is a GET navigation endpoint and is therefore exposed
/// to login-CSRF (a third party could force a platform admin's browser to mint a grant). The blast
/// radius is bounded: the grant cookie is HttpOnly and lives only in the victim's own browser, the
/// victim must already be a platform admin, the only effect is being navigated into a tenant, and
/// the mint is recorded in the auth audit log under the operator's real subject. Accepted as a
/// residual risk consistent with the existing OIDC-login endpoint shape.
/// </para>
/// </remarks>
[ApiController]
[Tags("PlatformAdmin")]
[Route("api/auth/platform-access")]
[AllowAnonymous]
public class PlatformAccessController(
    NocturneDbContext dbContext,
    IJwtService jwtService,
    IAuthAuditService authAudit,
    IOptions<OidcOptions> oidcOptions,
    IOptions<BaseDomainOptions> baseDomainOptions,
    ILogger<PlatformAccessController> logger) : ControllerBase
{
    /// <summary>Lifetime of a platform-access grant. A time-boxed "visit", not a standing credential.</summary>
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(30);

    private const string AuditEventType = "platform_admin_tenant_access";

    /// <summary>
    /// Mint a platform-access grant for the given tenant slug and redirect the operator into the
    /// tenant. Bounces through OIDC login if there is no session yet; 403s if the session is not a
    /// platform admin.
    /// </summary>
    /// <param name="tenant">Target tenant slug.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<IActionResult> Access([FromQuery] string? tenant, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant))
            return BadRequest(new { error = "missing_tenant" });

        var auth = HttpContext.GetAuthContext();

        // No session yet → send the operator through nocturne OIDC login, returning here.
        if (auth is not { IsAuthenticated: true, SubjectId: not null })
        {
            var returnUrl = $"/api/auth/platform-access?tenant={Uri.EscapeDataString(tenant)}";
            return Redirect($"/api/auth/oidc/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        if (!auth.IsPlatformAdmin)
        {
            await authAudit.LogAsync(AuditEventType, auth.SubjectId, success: false,
                ipAddress: GetClientIp(), userAgent: GetUserAgent(),
                errorMessage: "not_platform_admin",
                detailsJson: JsonSerializer.Serialize(new { slug = tenant }));
            logger.LogWarning("Subject {SubjectId} attempted platform access to '{Slug}' without platform_admin",
                auth.SubjectId, tenant);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "forbidden" });
        }

        // The tenants table is not RLS-scoped, so this resolves any tenant by slug.
        var target = await dbContext.Tenants.AsNoTracking()
            .Where(t => t.Slug == tenant)
            .Select(t => new { t.Id, t.Slug, t.IsActive })
            .FirstOrDefaultAsync(ct);

        if (target is null)
            return NotFound(new { error = "tenant_not_found" });

        if (!target.IsActive)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "tenant_inactive" });

        // Mint a tenant-pinned, platform-access-marked grant for the operator's real subject.
        var subject = new SubjectInfo
        {
            Id = auth.SubjectId.Value,
            Name = auth.SubjectName ?? string.Empty,
            Email = auth.Email,
        };
        var grant = jwtService.GenerateAccessToken(
            subject,
            permissions: ["*"],
            roles: [],
            scopes: [],
            clientId: null,
            limitTo24Hours: false,
            tenantId: target.Id,
            lifetime: GrantLifetime,
            platformAccess: true);

        var cookie = oidcOptions.Value.Cookie;
        Response.Cookies.Append(cookie.PlatformAccessName, grant, new CookieOptions
        {
            HttpOnly = true,
            Secure = cookie.Secure,
            SameSite = SessionCookieExtensions.MapSameSiteMode(cookie.SameSite),
            Path = cookie.Path,
            Domain = cookie.Domain,
            IsEssential = true,
            Expires = DateTimeOffset.UtcNow.Add(GrantLifetime),
        });

        await authAudit.LogAsync(AuditEventType, auth.SubjectId, success: true,
            ipAddress: GetClientIp(), userAgent: GetUserAgent(),
            detailsJson: JsonSerializer.Serialize(new { tenantId = target.Id, slug = target.Slug }));

        logger.LogInformation(
            "Platform-admin {SubjectId} was granted access to tenant {TenantId} ({Slug})",
            auth.SubjectId, target.Id, target.Slug);

        // Only honour a sensible forwarded scheme; never reflect an arbitrary client value
        // into the redirect. Prod is always behind TLS, so default to https.
        var forwardedProto = Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
        var proto = forwardedProto is "http" or "https" ? forwardedProto : "https";
        var redirectUrl = $"{proto}://{target.Slug}.{baseDomainOptions.Value.BaseDomain}/";
        return Redirect(redirectUrl);
    }

    private string? GetClientIp() =>
        Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
        ?? HttpContext.Connection.RemoteIpAddress?.ToString();

    private string? GetUserAgent() => Request.Headers.UserAgent.FirstOrDefault();
}
