namespace Nocturne.API.Authorization;

/// <summary>
/// Marks a controller or action as safe to invoke while the current tenant has not
/// yet completed first-time setup (no passkey credentials) or is in recovery mode.
/// <para>
/// <see cref="Middleware.TenantSetupMiddleware"/> and
/// <see cref="Middleware.RecoveryModeMiddleware"/> use endpoint metadata to
/// short-circuit blocking behavior when this attribute is present.
/// </para>
/// <para>
/// Apply sparingly — only to endpoints that must be reachable to bootstrap a tenant
/// (passkey/TOTP setup, OIDC bootstrap login, admin tenant provisioning, metadata).
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AllowDuringSetupAttribute : Attribute
{
}
