using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Deletes a subject's account. Before the row goes, it stops, in every tenant the subject is a
/// member of, what they made there: the guest links, device credentials and member invites are
/// revoked or capped and the alert channels they added stop. The delete clears the maker of what
/// it leaves, so nothing could find those by their maker afterwards.
/// </summary>
public interface ISubjectDeletion
{
    /// <summary>
    /// Deletes <paramref name="subjectId"/> with what they made. The stop commits per tenant before the
    /// delete, so a delete refused after it leaves the subject in place with nothing they made live.
    /// </summary>
    /// <param name="subjectId">The subject to delete.</param>
    /// <param name="evenLastOwner">
    /// Removes every membership first, refusing no last owner, as <see cref="Multitenancy.ITenantMembershipRepository.RemoveMembershipsOfSubjectAsync"/>
    /// does, and names the tenants it left without an owner.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>
    /// <see cref="SubjectChange.Changed"/> when deleted, <see cref="SubjectChange.LastOwnerOf"/> naming
    /// the tenants when the subject is the last owner of any, <see cref="SubjectChange.LastManagerOf"/>
    /// naming the teams when the subject is the last manager of any with other members,
    /// <see cref="SubjectChange.NotFound"/> when it does not exist or is a system subject.
    /// </returns>
    Task<SubjectDeletionOutcome> DeleteAsync(Guid subjectId, bool evenLastOwner, CancellationToken ct);
}
