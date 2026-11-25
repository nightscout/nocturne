using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Auth;

/// <inheritdoc cref="ISubjectDeletion"/>
/// <remarks>
/// The tenants are read before any membership goes, and each tenant's stop (<see cref="MadeByStop"/>)
/// runs on a unit of its own, since the tables it writes force row level security per tenant. Its
/// sessions are evicted once that unit commits. The teams and sites where the subject is staff are read
/// before anything goes, since their rows go with the account, and their owners' waiting notices are
/// recounted once it is deleted.
/// </remarks>
public sealed class SubjectDeletion(
    INocturneDbContextFactory contexts,
    ISubjectRepository subjects,
    ITenantMembershipRepository members,
    GrantRevocationService grantRevocation,
    IAuthAuditService audit,
    TimeProvider time,
    TeamStaffAnnouncer teamStaff,
    ILogger<SubjectDeletion> logger) : ISubjectDeletion
{
    /// <inheritdoc/>
    public async Task<SubjectDeletionOutcome> DeleteAsync(Guid subjectId, bool evenLastOwner, CancellationToken ct)
    {
        var subject = await subjects.GetAsync(subjectId, ct);
        if (subject is null)
            return new SubjectDeletionOutcome(new SubjectChange.NotFound(), []);
        if (subject.IsSystemSubject)
        {
            logger.LogWarning("Attempted to delete system subject {SubjectId}", subjectId);
            return new SubjectDeletionOutcome(new SubjectChange.NotFound(), []);
        }

        var staffNotices = await teamStaff.OwedForDeletionAsync(subjectId, ct);
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var tenantId in await members.ListTenantIdsOfSubjectAsync(subjectId, ct))
        {
            await using var unit = await DbUnitOfWork.OpenForTenantAsync(contexts, tenantId, ct);
            var revoked = await unit.CommitAsync(
                token => MadeByStop.StopAsync(unit, grantRevocation, tenantId, subjectId, now, token), ct: ct);
            grantRevocation.EvictSessions(revoked);
        }

        IReadOnlyList<LastOwnerTenant> leftWithoutOwner = evenLastOwner
            ? (await members.RemoveMembershipsOfSubjectAsync(subjectId, ct)).TenantsLeftWithoutOwner
            : [];

        var change = await subjects.DeleteAsync(subjectId, ct);
        if (change is SubjectChange.Changed)
        {
            logger.LogInformation("Deleted subject {SubjectId}", subjectId);
            await audit.LogAsync(AuthAuditEventType.SubjectDeleted, subjectId, success: true);
            await teamStaff.AnnounceAsync(staffNotices);
        }

        return new SubjectDeletionOutcome(change, leftWithoutOwner);
    }
}
