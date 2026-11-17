using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Middleware;

/// <summary>
/// Middleware that returns 503 for freshly provisioned tenants (no passkey
/// credentials) or tenants in recovery mode (orphaned subjects with no
/// passkey and no OIDC binding). Allows passkey setup, admin, and metadata
/// endpoints through so setup/recovery flows can complete. Trusted instance-key
/// service requests also bypass the gate (see <see cref="IInstanceKeyValidator"/>)
/// so automation can configure a tenant before a human owner registers a passkey.
/// When no tenant is resolved (e.g. tenantless cross-tenant paths, or zero-tenant setup),
/// the middleware passes through.
/// </summary>
/// <remarks>
/// Must run after <see cref="Multitenancy.TenantResolutionMiddleware"/>, which supplies the tenant,
/// and after <c>UseRouting</c>, which supplies the endpoint whose
/// <see cref="AllowDuringSetupAttribute"/> bypasses both the setup and the recovery check.
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
    /// <param name="members">The memberships the checks below read, under the repository's system pin.</param>
    /// <param name="instanceKeyValidator">Validator used to let trusted instance-key callers bypass the setup gate.</param>
    /// <returns>A task that completes when the middleware has finished processing.</returns>
    public async Task InvokeAsync(
        HttpContext context,
        ITenantAccessor tenantAccessor,
        ITenantMembershipRepository members,
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

        switch (await EvaluateGateAsync(members, tenantAccessor.TenantId, path, context.RequestAborted))
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
    /// The reads are system-pinned in the repository (<see cref="ITenantMembershipRepository.ReadSetupGateAsync"/>):
    /// the gate runs on public-share requests too. The counts and the orphan list are read only to
    /// log a refusal.
    /// </remarks>
    private async Task<SetupGate> EvaluateGateAsync(
        ITenantMembershipRepository members,
        Guid tenantId,
        string path,
        CancellationToken ct)
    {
        var gate = await members.ReadSetupGateAsync(tenantId, ct);

        if (gate is not { HasCredentials: true })
        {
            var counts = gate is null
                ? new Core.Models.Identity.SetupCredentialCounts(0, 0, 0)
                : await members.CountSetupCredentialsAsync(tenantId, ct);

            _logger.LogWarning(
                "Tenant {TenantId} setup check failed — returning 503 setup_required. " +
                "Path={Path}, MemberCount={MemberCount}, PasskeyCount={PasskeyCount}, OidcCount={OidcCount}",
                tenantId, path, counts.Members, counts.Passkeys, counts.OidcIdentities);

            return SetupGate.SetupRequired;
        }

        if (gate.HasOrphans)
        {
            var orphanedSubjects = (await members.ListOrphanedMembersAsync(tenantId, ct))
                .Select(s => new { Id = s.SubjectId, s.Name, s.Username })
                .ToList();

            _logger.LogWarning(
                "Tenant {TenantId} has orphaned subjects, returning 503 recovery_mode. " +
                "Path={Path}, OrphanedSubjects={@OrphanedSubjects}",
                tenantId, path, orphanedSubjects);

            return SetupGate.RecoveryMode;
        }

        return SetupGate.Pass;
    }
}
