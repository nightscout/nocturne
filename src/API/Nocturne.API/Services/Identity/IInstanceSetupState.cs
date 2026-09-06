namespace Nocturne.API.Services.Identity;

/// <summary>
/// Whether first-run setup has produced a usable account somewhere on the instance.
/// </summary>
/// <remarks>
/// The instance-wide counterpart to <see cref="Middleware.TenantSetupMiddleware"/>'s per-tenant
/// check. The tenantless surface — first-tenant creation, the slug availability probe — resolves
/// no tenant, so that middleware passes those requests through and cannot answer for them.
/// </remarks>
public interface IInstanceSetupState
{
    /// <summary>
    /// Whether any tenant on the instance has a member holding a real credential (a passkey or a
    /// linked OIDC identity).
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><see langword="true"/> once an account exists that someone can sign in with.</returns>
    Task<bool> IsSetupCompleteAsync(CancellationToken ct = default);

    /// <summary>
    /// The same question asked of one tenant: whether it has a member holding a passkey or a
    /// linked OIDC identity.
    /// </summary>
    /// <param name="tenantId">The tenant to ask about.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><see langword="true"/> when that tenant already has a signed-up account.</returns>
    Task<bool> TenantHasCredentialedMemberAsync(Guid tenantId, CancellationToken ct = default);
}
