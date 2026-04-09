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
/// Only active in multi-tenant mode (runs after TenantResolutionMiddleware).
/// Single-tenant setup/recovery is handled by RecoveryModeMiddleware.
/// </summary>
public class TenantSetupMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantSetupMiddleware> _logger;

    public TenantSetupMiddleware(
        RequestDelegate next,
        ILogger<TenantSetupMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

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

        // Check 1: Does this tenant have any members with passkey credentials?
        // PasskeyCredentialEntity is subject-scoped (not tenant-scoped), so we join through TenantMembers.
        var tenantId = tenantAccessor.TenantId;
        var hasCredentials = await db.TenantMembers
            .Where(m => m.TenantId == tenantId)
            .AnyAsync(m => db.PasskeyCredentials.Any(c => c.SubjectId == m.SubjectId));
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
