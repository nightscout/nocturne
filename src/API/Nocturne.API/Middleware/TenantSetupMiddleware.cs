using Microsoft.EntityFrameworkCore;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Middleware;

/// <summary>
/// Middleware that returns 503 for freshly provisioned tenants (no passkey
/// credentials) or tenants in recovery mode (orphaned subjects with no
/// passkey and no OIDC binding). Allows passkey setup, admin, and metadata
/// endpoints through so setup/recovery flows can complete. Trusted instance-key
/// service requests also bypass the gate (see <see cref="IInstanceKeyValidator"/>)
/// so automation can configure a tenant before a human owner registers a passkey.
///
/// Runs after TenantResolutionMiddleware. When no tenant is resolved
/// (e.g. tenantless cross-tenant paths, or zero-tenant setup), the
/// middleware passes through.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline order (position 4 of 6 custom middleware):
/// <see cref="JsonExtensionMiddleware"/>,
/// <see cref="OidcCallbackRedirectMiddleware"/>, <see cref="Multitenancy.TenantResolutionMiddleware"/>,
/// <b>TenantSetupMiddleware</b>, <see cref="AuthenticationMiddleware"/>,
/// <see cref="MemberScopeMiddleware"/>.
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
    /// <param name="dbFactory">Factory for the tenant-pinned context the checks below run on.</param>
    /// <param name="instanceKeyValidator">Validator used to let trusted instance-key callers bypass the setup gate.</param>
    /// <returns>A task that completes when the middleware has finished processing.</returns>
    public async Task InvokeAsync(
        HttpContext context,
        ITenantAccessor tenantAccessor,
        IDbContextFactory<NocturneDbContext> dbFactory,
        IInstanceKeyValidator instanceKeyValidator)
    {
        // Only check when a tenant has been resolved
        if (!tenantAccessor.IsResolved)
        {
            await _next(context);
            return;
        }

        // Demo tenants bypass setup requirements — they have no owner credentials by design
        if (tenantAccessor.Context?.IsDemo == true)
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

        // A valid instance-key service request (trusted in-cluster automation — a
        // provisioner or a headless setup agent) bypasses the setup/recovery gate.
        // The instance key is the highest-trust service credential and already grants
        // platform-admin once setup completes; letting it through here lets automation
        // stand up a tenant (connectors, settings, seed data) before a human owner has
        // registered a passkey. Untrusted tenant traffic still gets 503 until then.
        // A bare key without the X-Instance-Service marker does NOT qualify (see
        // IInstanceKeyValidator), so a key accidentally forwarded onto a browser
        // request cannot slip past the gate.
        if (instanceKeyValidator.Classify(context) == InstanceKeyRequestKind.Valid)
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

        // Evaluated in a scope of its own so the extra context is disposed before the rest of the
        // pipeline runs, rather than being held open for the whole downstream request.
        switch (await EvaluateGateAsync(dbFactory, tenantAccessor.TenantId, path, context.RequestAborted))
        {
            case SetupGate.SetupRequired:
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "setup_required",
                    message = "Initial setup required. Please register a passkey to secure your account.",
                    setupRequired = true,
                    recoveryMode = false,
                });
                return;

            case SetupGate.RecoveryMode:
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

    /// <summary>What the setup and recovery checks decided for this tenant.</summary>
    private enum SetupGate
    {
        /// <summary>The tenant is configured and has no locked-out accounts.</summary>
        Pass,

        /// <summary>No member holds a credential yet — first-run setup has not completed.</summary>
        SetupRequired,

        /// <summary>A member cannot sign in at all, so the recovery flow has to be reachable.</summary>
        RecoveryMode,
    }

    /// <summary>
    /// Runs the setup and recovery checks and logs the diagnostics for a refusal.
    /// </summary>
    /// <remarks>
    /// On its own context rather than the request-scoped one. A public-share request marks the
    /// scoped context as a share, and membership is not share-visible data: once tenant_members is
    /// behind Row Level Security, the restrictive share_category_read policy denies a share every
    /// row of it. Read there, this gate would see no members and answer 503 setup_required to every
    /// share request. Whether the instance is configured is a property of the instance, not of what
    /// a share may see, so the checks run on a plain tenant-pinned context that is never flagged as
    /// a share. Scoped to this method so the context is released before the pipeline continues.
    /// </remarks>
    private async Task<SetupGate> EvaluateGateAsync(
        IDbContextFactory<NocturneDbContext> dbFactory,
        Guid tenantId,
        string path,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateTenantPinnedContextAsync(tenantId, ct);

        // Check 1: Does this tenant have any members with auth credentials (passkey or OIDC)?
        // These entities are subject-scoped (not tenant-scoped), so we join through TenantMembers.
        var memberCount = await db.TenantMembers
            .Where(m => m.TenantId == tenantId)
            .CountAsync(ct);
        var hasCredentials = memberCount > 0 && await db.TenantMembers
            .Where(m => m.TenantId == tenantId)
            .AnyAsync(m =>
                db.PasskeyCredentials.Any(c => c.SubjectId == m.SubjectId) ||
                db.SubjectOidcIdentities.Any(i => i.SubjectId == m.SubjectId), ct);
        if (!hasCredentials)
        {
            var passkeyCount = memberCount > 0
                ? await db.TenantMembers
                    .Where(m => m.TenantId == tenantId)
                    .SelectMany(m => db.PasskeyCredentials.Where(c => c.SubjectId == m.SubjectId))
                    .CountAsync(ct)
                : 0;
            var oidcCount = memberCount > 0
                ? await db.TenantMembers
                    .Where(m => m.TenantId == tenantId)
                    .SelectMany(m => db.SubjectOidcIdentities.Where(i => i.SubjectId == m.SubjectId))
                    .CountAsync(ct)
                : 0;

            _logger.LogWarning(
                "Tenant {TenantId} setup check failed — returning 503 setup_required. " +
                "Path={Path}, MemberCount={MemberCount}, PasskeyCount={PasskeyCount}, OidcCount={OidcCount}, " +
                "DbContextTenantId={DbContextTenantId}",
                tenantId, path, memberCount, passkeyCount, oidcCount, db.TenantId);

            return SetupGate.SetupRequired;
        }

        // Check 2: Does this tenant have any orphaned subjects?
        // Subjects are not tenant-scoped — join through TenantMembers to scope to this tenant.
        var orphanedSubjects = await db.TenantMembers
            .Where(tm => tm.TenantId == tenantId)
            .Join(
                db.Subjects.Where(s => s.IsActive && !s.IsSystemSubject),
                tm => tm.SubjectId,
                s => s.Id,
                (tm, s) => s)
            .Where(s =>
                !db.SubjectOidcIdentities.Any(i => i.SubjectId == s.Id) &&
                !db.PasskeyCredentials.Any(p => p.SubjectId == s.Id))
            .Select(s => new { s.Id, s.Name, s.Username })
            .ToListAsync(ct);

        if (orphanedSubjects.Count > 0)
        {
            _logger.LogWarning(
                "Tenant {TenantId} has orphaned subjects — returning 503 recovery_mode. " +
                "Path={Path}, OrphanedSubjects={@OrphanedSubjects}",
                tenantId, path, orphanedSubjects);

            return SetupGate.RecoveryMode;
        }

        return SetupGate.Pass;
    }
}