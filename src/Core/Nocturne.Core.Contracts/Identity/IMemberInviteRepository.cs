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
    /// invitee, who is not shown them. Read pinned to <paramref name="tenantId"/>, so a sign-in on a
    /// host that resolves no tenant can check a join link of the sole tenant it serves.
    /// </summary>
    Task<MemberInviteRecord?> FindByTokenHashAsync(string tokenHash, Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Takes one use of invite <paramref name="inviteId"/> if it is unrevoked, still under its cap and
    /// made by a subject who is a member in force at <paramref name="now"/>, in one statement, so two
    /// accepts of a single-use invite cannot both join and an accept cannot pass a removal of the
    /// maker that revoked the invite first.
    /// </summary>
    Task<MemberInviteClaim> TryClaimUseAsync(Guid inviteId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Revokes at <paramref name="now"/> every invite of <paramref name="tenantId"/> that
    /// <paramref name="makerSubjectId"/> made and that can still be accepted, staging a
    /// <c>member_invite_revoked</c> audit row for each on the caller's unit of work, and returns their
    /// ids. Nothing is saved: the caller's save commits the revocations with the removal they belong to.
    /// </summary>
    Task<IReadOnlyList<Guid>> StageRevokeLiveMadeByAsync(
        Guid tenantId, Guid makerSubjectId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Takes back the revocations <see cref="StageRevokeLiveMadeByAsync"/> staged for
    /// <paramref name="inviteIds"/>, for a removal that turned out to be refused, so a later save of the
    /// unit does not write them. Their audit rows are the caller's to take back.
    /// </summary>
    void UnstageRevokeLiveMadeBy(IReadOnlyCollection<Guid> inviteIds);

    /// <summary>
    /// The subjects who made an invite of <paramref name="tenantId"/> and are members in force at
    /// <paramref name="now"/>, in one read. The rule of the use claim, so a membership whose reason has
    /// lapsed reads as absent until it is recomputed.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetMakersInForceAsync(Guid tenantId, DateTime now, CancellationToken ct);

    /// <summary>The invites of <paramref name="tenantId"/> with their uses, newest first.</summary>
    Task<IReadOnlyList<MemberInviteRecord>> GetForTenantAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Revokes invite <paramref name="inviteId"/> of <paramref name="tenantId"/> at <paramref name="now"/>.</summary>
    Task<MemberInviteRevocation> RevokeAsync(Guid inviteId, Guid tenantId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Whether the tenant has an invite that was accepted, or that can still be accepted at
    /// <paramref name="now"/>: unexpired, unrevoked, under its cap and made by a member in force.
    /// </summary>
    Task<bool> AnyUsedOrLiveAsync(DateTime now, CancellationToken ct);
}
