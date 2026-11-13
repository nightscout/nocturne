using Nocturne.Core.Models.Identity;

namespace Nocturne.Core.Contracts.Identity;

/// <summary>
/// Stores tenant creation invites. They belong to the instance, not to a tenant, so every method
/// reads and writes as the platform.
/// </summary>
public interface ITenantCreationInviteRepository
{
    /// <summary>Stores an invite and returns its id.</summary>
    Task<Guid> CreateAsync(NewTenantCreationInvite invite, CancellationToken ct);

    /// <summary>The invite whose token hashes to <paramref name="tokenHash"/>, without its uses.</summary>
    Task<TenantCreationInviteRecord?> FindByTokenHashAsync(string tokenHash, CancellationToken ct);

    /// <summary>Every invite, newest first, with the tenants each one created.</summary>
    Task<IReadOnlyList<TenantCreationInviteRecord>> ListAsync(CancellationToken ct);

    /// <summary>
    /// Takes one use of the invite in one statement, if at <paramref name="now"/> it is not revoked,
    /// not expired and under its cap, so two people redeeming a single-use link cannot both create
    /// a tenant. False when it could not be taken.
    /// </summary>
    Task<bool> TryClaimUseAsync(Guid inviteId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Gives back a use taken by <see cref="TryClaimUseAsync"/> whose tenant was never created.
    /// </summary>
    Task ReleaseUseAsync(Guid inviteId, CancellationToken ct);

    /// <summary>Records that the invite created <paramref name="tenantId"/>.</summary>
    Task RecordUseAsync(Guid inviteId, Guid tenantId, DateTime redeemedAt, CancellationToken ct);

    /// <summary>
    /// Revokes the invite. False when no invite has that id; revoking a revoked invite succeeds.
    /// </summary>
    Task<bool> RevokeAsync(Guid inviteId, DateTime now, CancellationToken ct);
}
