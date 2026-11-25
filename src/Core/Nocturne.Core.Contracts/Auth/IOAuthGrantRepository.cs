using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Stores OAuth grants: the consents, direct tokens and guest links of a tenant. A grant is
/// revoked by stamping it, never deleted, so the row stays as the audit trail of what its tokens
/// did. The tokens issued under a grant are on <see cref="IOAuthTokenRepository"/>.
/// </summary>
/// <remarks>
/// Every method runs as the scope's reader on its tenant, except the three that take a
/// <c>tenantId</c>, which pin it: they serve the authentication handlers and the hub
/// revalidation, which hold a scope whose context carries no tenant, and under FORCE ROW LEVEL
/// SECURITY an unpinned read there matches no row. A lookup returns null for a row the reader
/// cannot see; a write returning <c>bool</c> says whether it found a row to change.
/// </remarks>
public interface IOAuthGrantRepository
{
    /// <summary>
    /// The direct grant on <paramref name="tenantId"/> that <paramref name="lookup"/> identifies
    /// and that authenticates at <paramref name="now"/>, with its subject's membership of that
    /// tenant read in the same statement; null when there is none. A grant is live when it is not
    /// revoked and either open-ended or short of its expiry; the expiry instant itself does not
    /// authenticate. It authenticates only while its subject is active, so a deactivated subject's
    /// grant is refused as a revoked one is.
    /// </summary>
    /// <remarks>
    /// The grant's own tenant is matched as well as pinned, so the tenant a grant authorizes is
    /// decided here rather than by whatever tenant state the connection carries.
    /// </remarks>
    Task<AuthenticatedDirectGrant?> FindLiveDirectGrantAsync(
        Guid tenantId, DirectGrantLookup lookup, DateTime now, CancellationToken ct);

    /// <summary>The state of grant <paramref name="grantId"/> on <paramref name="tenantId"/>; null when it is not there.</summary>
    Task<OAuthGrantState?> GetStateAsync(Guid tenantId, Guid grantId, CancellationToken ct);

    /// <summary>
    /// Stamps when, and when <paramref name="presentation"/> is given from where and by what, grant
    /// <paramref name="grantId"/> on <paramref name="tenantId"/> was presented, in one statement that
    /// writes only when the stored stamp is due (<see cref="Nocturne.Core.Models.Authorization.LastUsedStamp"/>).
    /// </summary>
    Task RecordUseAsync(
        Guid tenantId, Guid grantId, DateTime now, GrantPresentation? presentation, CancellationToken ct);

    /// <summary>
    /// Stamps when, and when <paramref name="presentation"/> is given from where and by what, grant
    /// <paramref name="grantId"/> was presented, in one statement that writes only when the stored
    /// stamp is due (<see cref="Nocturne.Core.Models.Authorization.LastUsedStamp"/>).
    /// </summary>
    Task RecordUseAsync(Guid grantId, DateTime now, GrantPresentation? presentation, CancellationToken ct);

    /// <summary>The grant with <paramref name="grantId"/>, in any state.</summary>
    Task<OAuthGrantRecord?> GetAsync(Guid grantId, CancellationToken ct);

    /// <summary>
    /// The unrevoked app grant of <paramref name="subjectId"/> for client <paramref name="clientEntityId"/>:
    /// the one grant a consent merges into, which a unique index allows a subject at most one of.
    /// </summary>
    Task<OAuthGrantRecord?> FindActiveAsync(Guid clientEntityId, Guid subjectId, CancellationToken ct);

    /// <summary>The unrevoked grants of <paramref name="subjectId"/>, of every type, newest first.</summary>
    Task<IReadOnlyList<OAuthGrantRecord>> GetActiveForSubjectAsync(Guid subjectId, CancellationToken ct);

    /// <summary>
    /// The unrevoked grants of <paramref name="subjectId"/> and, in any state and of any subject, the
    /// grants <paramref name="writerIds"/> name, newest first.
    /// </summary>
    Task<IReadOnlyList<OAuthGrantRecord>> ListForConnectionsAsync(
        Guid? subjectId, IReadOnlyCollection<Guid> writerIds, CancellationToken ct);

    /// <summary>
    /// The app grants, in any state and of any subject, of the clients <paramref name="clientEntityIds"/>
    /// and of every client whose software id is one of <paramref name="softwareIds"/>.
    /// </summary>
    Task<IReadOnlyList<OAuthGrantRecord>> ListAppGrantsOfClientsAsync(
        IReadOnlyCollection<Guid> clientEntityIds, IReadOnlyCollection<string> softwareIds, CancellationToken ct);

    /// <summary>The grant <paramref name="grantId"/> owned by <paramref name="ownerSubjectId"/>, in any state.</summary>
    Task<OAuthGrantRecord?> FindOfSubjectAsync(Guid grantId, Guid ownerSubjectId, CancellationToken ct);

    /// <summary>
    /// The unrevoked direct grants, newest first: of <paramref name="subjectId"/> when one is
    /// given, else of the whole tenant; and when <paramref name="liveAt"/> is given, only those
    /// live at that instant (see <see cref="OAuthGrantRecord.IsLiveDirectGrantAt"/>).
    /// </summary>
    Task<IReadOnlyList<OAuthGrantRecord>> ListDirectAsync(Guid? subjectId, DateTime? liveAt, CancellationToken ct);

    /// <summary>
    /// The direct grant <paramref name="lookup"/> identifies if live at <paramref name="now"/>, read
    /// as the scope's reader. The handlers' pinned read is
    /// <see cref="FindLiveDirectGrantAsync(Guid, DirectGrantLookup, DateTime, CancellationToken)"/>.
    /// </summary>
    Task<OAuthGrantRecord?> FindLiveDirectGrantAsync(DirectGrantLookup lookup, DateTime now, CancellationToken ct);

    /// <summary>
    /// The oldest direct grant live at <paramref name="now"/> that matches the legacy secret hash
    /// <paramref name="legacySecretHash"/>, or null when none does: a revoked or expired one no longer
    /// holds the secret.
    /// </summary>
    Task<OAuthGrantRecord?> FindLiveLegacySecretAsync(string legacySecretHash, DateTime now, CancellationToken ct);

    /// <summary>The token hashes of every grant of the tenant that has one.</summary>
    Task<IReadOnlySet<string>> GetTokenHashesAsync(CancellationToken ct);

    /// <summary>The guest links of <paramref name="dataOwnerSubjectId"/>, newest first.</summary>
    Task<IReadOnlyList<OAuthGrantRecord>> GetGuestLinksAsync(
        Guid dataOwnerSubjectId, bool includeDismissed, CancellationToken ct);

    /// <summary>
    /// The guest links <paramref name="createdBySubjectId"/> made that are unrevoked and unexpired at
    /// <paramref name="now"/>, whoever the data owner is.
    /// </summary>
    Task<IReadOnlyList<OAuthGrantRecord>> GetLiveGuestLinksCreatedByAsync(
        Guid createdBySubjectId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Removes the expiry and the maker's stamp of device credential <paramref name="grantId"/>, an
    /// unrevoked direct grant a member made for the tenant's device subject, and stages a
    /// <c>device_credential_kept</c> audit row naming that member. With the stamp gone no later end
    /// date, removal or rejoin of the maker caps it again.
    /// </summary>
    /// <returns>
    /// False when <paramref name="grantId"/> is not an unrevoked device credential of the tenant that
    /// carries a maker's stamp, such as one a platform administrator gave an expiry.
    /// </returns>
    Task<bool> KeepDeviceCredentialAsync(Guid grantId, CancellationToken ct);

    /// <summary>
    /// Stages a <c>guest_link_revoked</c> audit row for each of <paramref name="links"/>, which the
    /// caller revokes because <paramref name="memberSubjectId"/>, who made them, was removed. Stages on
    /// the caller's unit of work and does not save.
    /// </summary>
    /// <exception cref="InvalidOperationException">The repository is not over a unit of work.</exception>
    void StageMemberRemovalGuestLinkAudit(IEnumerable<OAuthGrantRecord> links, Guid memberSubjectId);

    /// <summary>How many guest links of <paramref name="dataOwnerSubjectId"/> are unrevoked and unexpired at <paramref name="now"/>.</summary>
    Task<int> CountLiveGuestLinksAsync(Guid dataOwnerSubjectId, DateTime now, CancellationToken ct);

    /// <summary>Whether the tenant has a guest code still redeemable at <paramref name="now"/>: not used, revoked or expired.</summary>
    Task<bool> HasRedeemableGuestCodeAsync(DateTime now, CancellationToken ct);

    /// <summary>Whether the tenant has a guest or follower grant unrevoked and unexpired at <paramref name="now"/>.</summary>
    Task<bool> HasLiveShareGrantAsync(DateTime now, CancellationToken ct);

    /// <summary>The display name of each of <paramref name="grantIds"/>' clients, for the grants that have one.</summary>
    Task<IReadOnlyDictionary<Guid, string?>> GetClientDisplayNamesAsync(
        IReadOnlyCollection<Guid> grantIds, CancellationToken ct);

    /// <summary>
    /// Stores a grant and returns its id. The record is read back with <see cref="GetAsync"/> once
    /// saved: on a unit of work the tenant is stamped only then.
    /// </summary>
    Task<Guid> CreateAsync(NewOAuthGrant grant, CancellationToken ct);

    /// <summary>Stores an adopted credential as a migrated direct grant and returns its id.</summary>
    Task<Guid> AdoptLegacyCredentialAsync(AdoptedLegacyCredential credential, CancellationToken ct);

    /// <summary>Applies <paramref name="edit"/> to grant <paramref name="grantId"/> and returns it as edited, with its client.</summary>
    Task<OAuthGrantRecord?> UpdateAsync(Guid grantId, OAuthGrantEdit edit, CancellationToken ct);

    /// <summary>
    /// Takes the transaction-scoped lock that serialises the consents of <paramref name="subjectId"/> to
    /// client <paramref name="clientEntityId"/> on this tenant, so a consent that waits on it finds the
    /// grant the one ahead of it wrote. Then it holds the subject's row <c>FOR KEY SHARE</c> and takes
    /// the subject's membership lock, which the removal of the membership holds while it reads the
    /// grants it revokes. All three are held to the commit of the caller's unit of work.
    /// </summary>
    /// <returns>
    /// Whether the subject holds a membership of this tenant, read under the membership lock: a
    /// removal that committed first leaves none, and one that follows waits for this unit's commit and
    /// revokes the grant it writes, so a subject with no membership of a tenant holds no grant on it.
    /// </returns>
    /// <remarks>
    /// The subject row comes before the membership lock because account deletion and
    /// <c>RemoveMembershipsOfSubjectAsync</c> lock that row <c>FOR UPDATE</c> and then wait for the
    /// membership locks, while the grant's insert needs the row <c>FOR KEY SHARE</c> through its
    /// foreign key: taking it later would let a consent and a deletion each wait for what the other
    /// holds.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The repository is not over a unit of work, or the unit has no transaction open.</exception>
    Task<bool> LockConsentAsync(Guid clientEntityId, Guid subjectId, CancellationToken ct);

    /// <summary>
    /// Merges a consent into grant <paramref name="grantId"/> while it is unrevoked: the grant's row is
    /// held against a revocation and an edit of its scopes until the unit commits, and the scopes of
    /// <paramref name="scopes"/> join the ones the row holds once it is held. Writes nothing when the
    /// grant is revoked or gone.
    /// </summary>
    /// <param name="grantId">The grant to merge into.</param>
    /// <param name="scopes">The scopes consented to, added to the grant's.</param>
    /// <param name="limitTo24Hours">The latest consent sets whether tokens read only the last 24 hours.</param>
    /// <param name="approvedByDeviceCode">Whether a device-code approval is consenting. Once set it stays set.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The grant as merged; null when it was revoked or is not there, so nothing was written.</returns>
    /// <exception cref="InvalidOperationException">The repository is not over a unit of work, or the unit has no transaction open.</exception>
    Task<OAuthGrantRecord?> MergeConsentAsync(
        Guid grantId, IEnumerable<string> scopes, bool limitTo24Hours, bool approvedByDeviceCode, CancellationToken ct);

    /// <summary>
    /// Activates the guest link whose code hashes to <paramref name="codeHash"/> if it is
    /// redeemable at <paramref name="now"/>, recording where from, and returns it activated; null
    /// when no such code is redeemable.
    /// </summary>
    Task<OAuthGrantRecord?> ActivateGuestAsync(
        string codeHash, DateTime now, GrantPresentation presentation, CancellationToken ct);

    /// <summary>Dismisses grant <paramref name="grantId"/> from the list at <paramref name="now"/>.</summary>
    Task<bool> DismissAsync(Guid grantId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Revokes at <paramref name="now"/> those of <paramref name="grantIds"/> not yet revoked, and
    /// returns how many. A grant's other consequences are the caller's.
    /// </summary>
    Task<int> RevokeAsync(IReadOnlyCollection<Guid> grantIds, DateTime now, CancellationToken ct);
}

/// <summary>A direct grant that authenticated, with its subject's membership of the tenant.</summary>
/// <param name="Membership">Null when the subject holds no membership there.</param>
public sealed record AuthenticatedDirectGrant(DirectGrantCredential Grant, DirectGrantMembership? Membership);

/// <summary>A membership as read beside the grant it bounds.</summary>
/// <param name="Id">The membership row.</param>
/// <param name="Access">What the membership grants on its tenant.</param>
/// <param name="Direct">What its direct reason grants (<see cref="TenantMembershipFacts.Direct"/>).</param>
/// <param name="LastUsedAt">When the membership was last stamped as used.</param>
public sealed record DirectGrantMembership(Guid Id, TenantMemberAccess Access, TenantMemberAccess? Direct, DateTime? LastUsedAt);
