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
    /// cannot be written leaves the subject pending. The owners' notifications for the request
    /// are archived after the commit. With no tenant the subject is approved and joins none.
    /// </summary>
    /// <param name="granterScopes">
    /// The approving caller's resolved scopes: the grant ceiling the requested roles are checked against.
    /// </param>
    Task<ApproveAccessRequestResult> ApproveAsync(
        Guid subjectId,
        Guid? tenantId,
        List<Guid> roleIds,
        List<string>? directPermissions,
        bool limitTo24Hours,
        IReadOnlyCollection<string> granterScopes,
        CancellationToken ct = default);
}
