using Nocturne.Core.Models.Identity;

namespace Nocturne.Core.Contracts.Identity;

/// <summary>
/// Stores member invites: the bearer credentials that admit a subject to one tenant's membership.
/// </summary>
/// <remarks>
/// Every method runs as the scope's reader on its tenant. An invite is matched on the tenant the
/// caller names as well, so a token minted for another tenant reads as unknown rather than as an
/// invite that happens to point elsewhere.
/// </remarks>
public interface IMemberInviteRepository
{
    /// <summary>Stores an invite and returns its id.</summary>
    Task<Guid> CreateAsync(NewMemberInvite invite, CancellationToken ct);

    /// <summary>
    /// The invite of <paramref name="tenantId"/> whose token hashes to <paramref name="tokenHash"/>,
    /// with its tenant's and creator's names. Its uses are not read: the token lookup serves the
    /// invitee, who is not shown them.
    /// </summary>
    Task<MemberInviteRecord?> FindByTokenHashAsync(string tokenHash, Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Takes one use of invite <paramref name="inviteId"/> if it is still under its cap, in one
    /// statement, so two accepts of a single-use invite cannot both join. Returns <c>false</c> when
    /// the cap is reached.
    /// </summary>
    Task<bool> TryClaimUseAsync(Guid inviteId, CancellationToken ct);

    /// <summary>The invites of <paramref name="tenantId"/> with their uses, newest first.</summary>
    Task<IReadOnlyList<MemberInviteRecord>> GetForTenantAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Revokes invite <paramref name="inviteId"/> of <paramref name="tenantId"/> at <paramref name="now"/>.</summary>
    Task<MemberInviteRevocation> RevokeAsync(Guid inviteId, Guid tenantId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Whether the tenant has an invite that was accepted, or that can still be accepted at
    /// <paramref name="now"/>.
    /// </summary>
    Task<bool> AnyUsedOrLiveAsync(DateTime now, CancellationToken ct);
}
