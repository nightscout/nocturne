using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Middleware.Handlers;

/// <summary>
/// Authentication handler for platform-admin tenant-access grants. Reads the short-lived,
/// tenant-pinned JWT in the platform-access cookie (minted by <c>PlatformAccessController</c>)
/// and authenticates the request as <see cref="AuthType.PlatformAccess"/> — but only when the
/// grant's <c>platform_access</c> marker is set AND its pinned tenant matches the tenant resolved
/// from the request's subdomain.
/// </summary>
/// <remarks>
/// Runs before <see cref="SessionCookieHandler"/> (priority 40 &lt; 50) so an active grant takes
/// precedence over the operator's ordinary session on the granted tenant. If there is no grant
/// cookie, the token is invalid, the marker is missing, or the pinned tenant doesn't match the
/// resolved subdomain, this handler <see cref="AuthResult.Skip">skips</see> so the chain falls
/// through to the normal session (which, for a non-member, is then rejected by the membership
/// check in <see cref="AuthenticationMiddleware"/>). Requiring the <c>platform_access</c> marker
/// — not just a tenant pin — prevents any other tenant-pinned token (e.g. an OAuth token) from
/// being moved into this cookie to escalate to superuser.
/// </remarks>
/// <seealso cref="SessionCookieHandler"/>
/// <seealso cref="AuthenticationMiddleware"/>
public class PlatformAccessCookieHandler : IAuthHandler
{
    /// <summary>
    /// Handler priority (40 — runs before <see cref="SessionCookieHandler"/> at 50).
    /// </summary>
    public int Priority => 40;

    /// <summary>
    /// Handler name for logging.
    /// </summary>
    public string Name => "PlatformAccessCookieHandler";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PlatformAccessCookieHandler> _logger;
    private readonly OidcOptions _options;

    /// <summary>
    /// Creates a new instance of <see cref="PlatformAccessCookieHandler"/>.
    /// </summary>
    public PlatformAccessCookieHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<PlatformAccessCookieHandler> logger,
        IOptions<OidcOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
        var grant = context.Request.Cookies[_options.Cookie.PlatformAccessName];
        if (string.IsNullOrEmpty(grant))
            return AuthResult.Skip();

        // A grant is only meaningful on a resolved tenant subdomain.
        if (context.Items["TenantContext"] is not TenantContext tenant)
            return AuthResult.Skip();

        using var scope = _scopeFactory.CreateScope();
        var jwtService = scope.ServiceProvider.GetRequiredService<IJwtService>();
        var result = jwtService.ValidateAccessToken(grant);

        if (!result.IsValid || result.Claims is null)
            return AuthResult.Skip();

        var claims = result.Claims;

        // Must be a genuine platform-access grant pinned to THIS tenant. Anything else
        // (a normal session, an OAuth tenant-pinned token, a grant for another tenant)
        // falls through to the normal auth chain.
        if (!claims.PlatformAccess || claims.TenantId != tenant.TenantId)
            return AuthResult.Skip();

        // Defense in depth: a self-contained grant can't be revoked, so confirm the subject
        // is STILL a platform admin on every request. Revoking the flag then ends access on
        // the next request instead of lingering until the grant expires.
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        var stillPlatformAdmin = await db.Subjects
            .Where(s => s.Id == claims.SubjectId)
            .Select(s => s.IsPlatformAdmin)
            .FirstOrDefaultAsync();

        if (!stillPlatformAdmin)
        {
            _logger.LogWarning(
                "Platform-access grant rejected: subject {SubjectId} is no longer a platform admin",
                claims.SubjectId);
            return AuthResult.Skip();
        }

        _logger.LogInformation(
            "Platform-admin access grant accepted for subject {SubjectId} on tenant {TenantId}",
            claims.SubjectId, tenant.TenantId);

        return AuthResult.Success(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.PlatformAccess,
            SubjectId = claims.SubjectId,
            SubjectName = claims.Name,
            Email = claims.Email,
            Permissions = ["*"],
            Roles = claims.Roles,
            RawToken = grant,
            TokenId = Guid.TryParse(claims.JwtId, out var jwtId) ? jwtId : null,
            ExpiresAt = claims.ExpiresAt,
        });
    }
}
