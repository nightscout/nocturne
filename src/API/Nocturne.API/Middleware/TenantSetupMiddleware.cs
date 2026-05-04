using Microsoft.EntityFrameworkCore;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Middleware;

/// <summary>
/// Middleware that returns 503 for freshly provisioned tenants (no passkey
/// credentials) or tenants in recovery mode (orphaned subjects with no
/// passkey and no OIDC binding). Allows passkey setup, admin, and metadata
/// endpoints through so setup/recovery flows can complete.
///
/// Runs after TenantResolutionMiddleware. When no tenant is resolved
/// (e.g. tenantless cross-tenant paths, or zero-tenant setup), the
/// middleware passes through.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline order (position 4 of 7 custom middleware):
/// <see cref="JsonExtensionMiddleware"/>,
/// <see cref="OidcCallbackRedirectMiddleware"/>, <see cref="Multitenancy.TenantResolutionMiddleware"/>,
/// <b>TenantSetupMiddleware</b>, <see cref="AuthenticationMiddleware"/>,
/// <see cref="MemberScopeMiddleware"/>, <see cref="SiteSecurityMiddleware"/>.
/// </para>
/// <para>
/// Endpoints decorated with <see cref="AllowDuringSetupAttribute"/> bypass both the
/// setup check and the recovery check. Depends on <see cref="ITenantAccessor"/> to
/// determine whether a tenant has been resolved.
/// </para>
/// </remarks>
/// <seealso cref="AllowDuringSetupAttribute"/>
/// <seealso cref="Multitenancy.TenantResolutionMiddleware"/>
public class TenantSetupMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantSetupMiddleware> _logger;

    /// <summary>
    /// Creates a new instance of <see cref="TenantSetupMiddleware"/>.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">Logger for setup/recovery diagnostics.</param>
    public TenantSetupMiddleware(
        RequestDelegate next,
        ILogger<TenantSetupMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Checks whether the resolved tenant requires initial setup or is in recovery mode,
    /// returning 503 if API traffic should be blocked.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="tenantAccessor">Accessor for the resolved tenant identity.</param>
    /// <param name="db">Database context for querying passkey credentials and orphaned subjects.</param>
    /// <returns>A task that completes when the middleware has finished processing.</returns>
    public async Task InvokeAsync(
        HttpContext context,
        ITenantAccessor tenantAccessor,
        NocturneDbContext db)
    {
        // Only check when a tenant has been resolved
        if (!tenantAccessor.IsResolved)
        {
            await _next(context);
            return;
        }

        // Endpoints marked [AllowDuringSetup] bypass both the setup check and the
        // recovery check — these are the bootstrap endpoints (passkey/TOTP setup,
        // OIDC bootstrap login, admin provisioning, metadata).
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<AllowDuringSetupAttribute>() is not null)
        {
            await _next(context);
            return;
        }

        // Only block API paths; static files and non-API endpoints pass through.
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Check 1: Does this tenant have any members with auth credentials (passkey or OIDC)?
        // These entities are subject-scoped (not tenant-scoped), so we join through TenantMembers.
        var tenantId = tenantAccessor.TenantId;
        var hasCredentials = await db.TenantMembers
            .Where(m => m.TenantId == tenantId)
            .AnyAsync(m =>
                db.PasskeyCredentials.Any(c => c.SubjectId == m.SubjectId) ||
                db.SubjectOidcIdentities.Any(i => i.SubjectId == m.SubjectId));
        if (!hasCredentials)
        {
            _logger.LogDebug(
                "Tenant {TenantId} has no passkey credentials — returning setup required",
                tenantAccessor.TenantId);

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "setup_required",
                message = "Initial setup required. Please register a passkey to secure your account.",
                setupRequired = true,
                recoveryMode = false,
            });
            return;
        }

        // Check 2: Does this tenant have any orphaned subjects?
        // Subjects are not tenant-scoped — join through TenantMembers to scope to this tenant.
        var hasOrphaned = await db.TenantMembers
            .Where(tm => tm.TenantId == tenantId)
            .Join(
                db.Subjects.Where(s => s.IsActive && !s.IsSystemSubject),
                tm => tm.SubjectId,
                s => s.Id,
                (tm, s) => s)
            .Where(s =>
                !db.SubjectOidcIdentities.Any(i => i.SubjectId == s.Id) &&
                !db.PasskeyCredentials.Any(p => p.SubjectId == s.Id))
            .AnyAsync();

        if (hasOrphaned)
        {
            _logger.LogDebug(
                "Tenant {TenantId} has orphaned subjects — returning recovery mode",
                tenantAccessor.TenantId);

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "recovery_mode_active",
                message = "Instance is in recovery mode. Please register a passkey or authenticator app to continue.",
                setupRequired = false,
                recoveryMode = true,
            });
            return;
        }

        await _next(context);
    }
}
