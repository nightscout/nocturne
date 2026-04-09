using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;

namespace Nocturne.API.Middleware;

/// <summary>
/// Middleware that enforces recovery mode restrictions when active.
/// In multi-tenant mode, this middleware is a no-op — per-tenant recovery
/// is handled by TenantSetupMiddleware (which runs after tenant resolution).
/// In single-tenant mode, blocks API traffic when the global RecoveryModeState
/// is active, allowing only passkey/TOTP setup endpoints through.
/// The NOCTURNE_RECOVERY_MODE env var override bypasses the multi-tenant skip.
/// </summary>
public class RecoveryModeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RecoveryModeMiddleware> _logger;

    public RecoveryModeMiddleware(
        RequestDelegate next,
        ILogger<RecoveryModeMiddleware> logger
    )
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        RecoveryModeState state,
        IOptions<MultitenancyConfiguration> multitenancyConfig)
    {
        if (!state.IsEnabled && !state.IsSetupRequired)
        {
            await _next(context);
            return;
        }

        // In multi-tenant mode, per-tenant recovery is handled by TenantSetupMiddleware.
        // Only the env var override still triggers the global gate.
        var isMultiTenant = !string.IsNullOrEmpty(multitenancyConfig.Value.BaseDomain);
        var envOverride = string.Equals(
            Environment.GetEnvironmentVariable("NOCTURNE_RECOVERY_MODE"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (isMultiTenant && !envOverride)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";

        // Allow passkey, TOTP, metadata, and slug validation endpoints
        if (path.StartsWith("/api/auth/passkey/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/auth/totp/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/metadata", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/admin/tenants/validate-slug", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v4/admin/tenants/validate-slug", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/v4/me/tenants/validate-slug", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Block other API endpoints with a clear message
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            var mode = state.IsSetupRequired ? "setup" : "recovery";
            _logger.LogDebug("{Mode} mode: blocking request to {Path}", mode, path);

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                error = state.IsSetupRequired ? "setup_required" : "recovery_mode_active",
                message = state.IsSetupRequired
                    ? "Initial setup required. Please register a passkey or authenticator app."
                    : "Instance is in recovery mode. Please register a passkey or authenticator app to continue.",
                setupRequired = state.IsSetupRequired,
                recoveryMode = state.IsEnabled,
            });
            return;
        }

        // Allow non-API requests (frontend assets, health checks, etc.)
        await _next(context);
    }
}
