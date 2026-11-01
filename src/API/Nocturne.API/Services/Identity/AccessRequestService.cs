using Microsoft.EntityFrameworkCore;
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
    ILogger<AccessRequestService> logger) : IAccessRequestService
{
    private const string PendingStatus = "Pending";

    private static readonly SubjectEdit Approval = new(IsActive: true, ApprovalStatus: "Approved");

    /// <inheritdoc />
    public async Task<ApproveAccessRequestResult> ApproveAsync(
        Guid subjectId,
        Guid? tenantId,
        List<Guid> roleIds,
        List<string>? directPermissions,
        bool limitTo24Hours,
        IReadOnlyCollection<string> granterScopes,
        CancellationToken ct = default)
    {
        var subject = await subjects.GetAsync(subjectId, ct);
        if (subject is not { ApprovalStatus: PendingStatus })
            return new ApproveAccessRequestResult(ApproveAccessRequestOutcome.NotFound);

        if (roleIds.Count == 0 && directPermissions is not { Count: > 0 })
            return new ApproveAccessRequestResult(ApproveAccessRequestOutcome.NothingGranted);

        if (tenantId is not { } tenant)
        {
            await subjects.UpdateAsync(subjectId, Approval, ct);
            LogApproved(subjectId, subject.Name, roleIds.Count);
            return new ApproveAccessRequestResult(ApproveAccessRequestOutcome.Approved);
        }

        var roleGrant = await tenantRoleService.ValidateRoleGrantAsync(tenant, roleIds, granterScopes, ct);
        if (!roleGrant.Ok)
            return new ApproveAccessRequestResult(ApproveAccessRequestOutcome.RoleGrantRefused, roleGrant);

        await using (var unit = await DbUnitOfWork.OpenForTenantAsync(contexts, tenant, ct))
        {
            if (await new SubjectRepository(unit).UpdateAsync(subjectId, Approval, ct) is SubjectChange.NotFound)
                return new ApproveAccessRequestResult(ApproveAccessRequestOutcome.NotFound);

            if (!await new TenantMembershipRepository(unit).AddMemberAsync(
                    new NewTenantMember(
                        tenant, subjectId, roleIds, directPermissions, LimitTo24Hours: limitTo24Hours),
                    ct))
                return new ApproveAccessRequestResult(ApproveAccessRequestOutcome.AlreadyMember);

            try
            {
                await unit.CommitAsync(token => unit.Context.SaveChangesAsync(token), ct);
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
                "passkey.anonymous_login_request",
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
