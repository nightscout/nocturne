using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Identity;

/// <inheritdoc cref="IAccessRequestService"/>
public class AccessRequestService(
    ISubjectRepository subjects,
    ITenantMembershipRepository members,
    INocturneDbContextFactory contexts,
    ITenantRoleService tenantRoleService,
    IInAppNotificationService notificationService,
    TimeProvider time,
    ILogger<AccessRequestService> logger,
    IAccountLinkRoute linkRoute,
    GrantRevocationService grantRevocation,
    TeamStaffAnnouncer teamStaff) : IAccessRequestService
{
    /// <inheritdoc />
    public async Task<ApproveAccessRequestResult> ApproveAsync(
        Guid subjectId,
        Guid? tenantId,
        List<Guid> roleIds,
        List<string>? directPermissions,
        bool limitTo24Hours,
        IReadOnlyCollection<string> granterScopes,
        Guid? decidedBySubjectId,
        CancellationToken ct = default)
    {
        var subject = await subjects.GetAsync(subjectId, ct);
        if (subject is not { ApprovalStatus: nameof(ApprovalStatus.Pending) })
            return new ApproveAccessRequestResult(ApproveAccessRequestOutcome.NotFound);

        if (roleIds.Count == 0 && directPermissions is not { Count: > 0 })
            return new ApproveAccessRequestResult(ApproveAccessRequestOutcome.NothingGranted);

        if (tenantId is not { } tenant)
        {
            await subjects.UpdateAsync(subjectId, SubjectEdit.AccessApproved, ct);
            LogApproved(subjectId, subject.Name, roleIds.Count);
            return new ApproveAccessRequestResult(ApproveAccessRequestOutcome.Approved);
        }

        var roleGrant = await tenantRoleService.ValidateRoleGrantAsync(tenant, roleIds, granterScopes, ct);
        if (!roleGrant.Ok)
            return new ApproveAccessRequestResult(ApproveAccessRequestOutcome.RoleGrantRefused, roleGrant);

        await using (var unit = await DbUnitOfWork.OpenForTenantAsync(contexts, tenant, ct))
        {
            if (await new SubjectRepository(unit, linkRoute).UpdateAsync(subjectId, SubjectEdit.AccessApproved, ct) is SubjectChange.NotFound)
                return new ApproveAccessRequestResult(ApproveAccessRequestOutcome.NotFound);

            var tenantMembers = new TenantMembershipRepository(unit, linkRoute);
            IReadOnlyList<OAuthGrantRecord> lost = [];
            try
            {
                var added = await unit.CommitAsync(async token =>
                {
                    await TenantMembershipRepository.LockOwnerChangeAsync(unit, tenant, subjectId, token);
                    if (!await tenantMembers.AddDirectMemberAsync(
                            new NewTenantMember(
                                tenant, subjectId, roleIds, directPermissions, LimitTo24Hours: limitTo24Hours)
                                { How = MembershipJoinHow.Request, By = decidedBySubjectId },
                            time.GetUtcNow().UtcDateTime,
                            token))
                        return false;

                    if (await tenantMembers.FindLatestRequestAsync(tenant, subjectId, token) is { Status: MembershipRequestRecord.Pending } request)
                    {
                        await tenantMembers.DecideRequestAsync(
                            tenant, request.Id,
                            new MembershipRequestDecision(MembershipRequestRecord.Approved, decidedBySubjectId, time.GetUtcNow().UtcDateTime, roleIds),
                            token);
                    }

                    await unit.Context.SaveChangesAsync(token);
                    lost = await TeamReasonWrites.SyncOwnerChangeAsync(
                        unit, grantRevocation, tenant, subjectId, time.GetUtcNow().UtcDateTime, token);
                    return true;
                }, ct: ct);
                if (!added)
                    return new ApproveAccessRequestResult(ApproveAccessRequestOutcome.AlreadyMember);
                grantRevocation.EvictSessions(lost);
                await teamStaff.RecountOwnerChangeAsync(tenant, subjectId);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new ApproveAccessRequestResult(ApproveAccessRequestOutcome.NotFound);
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                return new ApproveAccessRequestResult(ApproveAccessRequestOutcome.AlreadyMember);
            }
        }

        LogApproved(subjectId, subject.Name, roleIds.Count);

        foreach (var ownerId in await members.GetOwnerSubjectIdsAsync(tenant, ct))
        {
            await notificationService.ArchiveBySourceAsync(
                ownerId.ToString(),
                NotificationSubjectNaming.AnonymousLoginRequest,
                subjectId.ToString(),
                NotificationArchiveReason.Completed,
                ct);
        }

        return new ApproveAccessRequestResult(ApproveAccessRequestOutcome.Approved);
    }

    private void LogApproved(Guid subjectId, string name, int roleCount) =>
        logger.LogInformation(
            "Access request approved: subject {SubjectId} ({Name}) assigned {RoleCount} roles",
            subjectId, name, roleCount);
}
