using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Core.Contracts.Identity;

/// <summary>Why <see cref="IAccessRequestService.ApproveAsync"/> did or did not approve.</summary>
public enum ApproveAccessRequestOutcome
{
    Approved,
    NotFound,
    NothingGranted,
    RoleGrantRefused,
    AlreadyMember,
}

/// <param name="Outcome">What happened; only <see cref="ApproveAccessRequestOutcome.Approved"/> wrote anything.</param>
/// <param name="RoleGrant">The validation that refused the roles, set with <see cref="ApproveAccessRequestOutcome.RoleGrantRefused"/>.</param>
public record ApproveAccessRequestResult(ApproveAccessRequestOutcome Outcome, RoleGrantValidation? RoleGrant = null);

/// <summary>
/// Actions the access requests a person leaves by signing up on a tenant host: a subject held
/// pending until a platform admin approves it.
/// </summary>
public interface IAccessRequestService
{
    /// <summary>
    /// Approves the pending subject and adds it to the tenant, in one commit: a membership that
    /// cannot be written leaves the subject pending. The subject's pending membership request on
    /// that tenant is decided as approved in the same commit. The owners' notifications for the
    /// request are archived after the commit. With no tenant the subject is approved and joins
    /// none; a request it filed stays pending for that tenant's owner to decide.
    /// </summary>
    /// <param name="granterScopes">
    /// The approving caller's resolved scopes: the grant ceiling the requested roles are checked against.
    /// </param>
    /// <param name="decidedBySubjectId">
    /// The approving caller, recorded on the membership request it decides; null for a platform
    /// administrator credential that names no subject.
    /// </param>
    Task<ApproveAccessRequestResult> ApproveAsync(
        Guid subjectId,
        Guid? tenantId,
        List<Guid> roleIds,
        List<string>? directPermissions,
        bool limitTo24Hours,
        IReadOnlyCollection<string> granterScopes,
        Guid? decidedBySubjectId,
        CancellationToken ct = default);
}
