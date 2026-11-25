using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// Runs the team writes that change team reasons (<see cref="TeamRepository.LockAccessStopAsync"/>,
/// <see cref="TeamRepository.LockStaffJoinAsync"/> and their siblings) on a unit of their own, applying
/// the changes they return as the API does, short of stopping what a member whose membership ends made
/// (the API's <c>MadeByStop</c>): the members these tests end made nothing.
/// </summary>
internal static class TeamWrites
{
    public static async Task<bool> StopAccessAsync(INocturneDbContextFactory contexts, Guid tenantId, Guid teamId)
    {
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(contexts, tenantId);
        return await unit.CommitAsync(async ct =>
            await TeamRepository.LockAccessStopAsync(unit, tenantId, teamId, ct) is { } changes && await ApplyAsync(unit, changes, ct, DateTime.UtcNow));
    }

    public static async Task<TeamDeletion> DeleteAsync(INocturneDbContextFactory contexts, Guid teamId, Guid callerSubjectId)
    {
        await using var unit = await DbUnitOfWork.OpenPlatformAsync(contexts);
        return await unit.CommitAsync(async ct =>
        {
            var (outcome, changes) = await TeamRepository.LockTeamDeletionAsync(unit, teamId, callerSubjectId, ct);
            if (changes is not null)
                await ApplyAsync(unit, changes, ct, DateTime.UtcNow);
            return outcome;
        });
    }

    public static async Task<bool> RemoveStaffAsync(
        INocturneDbContextFactory contexts, Guid teamId, Guid callerSubjectId, Guid staffSubjectId)
    {
        await using var unit = await DbUnitOfWork.OpenPlatformAsync(contexts);
        return await unit.CommitAsync(async ct =>
            await TeamRepository.LockStaffRemovalAsync(unit, teamId, callerSubjectId, staffSubjectId, ct) is { } changes
            && await ApplyAsync(unit, changes, ct, DateTime.UtcNow));
    }

    public static async Task<TeamInviteAcceptance> JoinStaffAsync(
        INocturneDbContextFactory contexts, string tokenHash, Guid subjectId, DateTime now)
    {
        await using var unit = await DbUnitOfWork.OpenPlatformAsync(contexts);
        return await unit.CommitAsync(async ct =>
        {
            var (acceptance, changes) = await TeamRepository.LockStaffJoinAsync(unit, tokenHash, subjectId, now, ct);
            if (changes is not null)
                await ApplyAsync(unit, changes, ct, now);
            return acceptance;
        });
    }

    public static async Task<TeamStaffChange> UpdateStaffAsync(
        INocturneDbContextFactory contexts, Guid teamId, Guid callerSubjectId, Guid staffSubjectId,
        TeamStaffNarrowing narrowing, DateTime now)
    {
        await using var unit = await DbUnitOfWork.OpenPlatformAsync(contexts);
        return await unit.CommitAsync(async ct =>
        {
            var (change, changes) = await TeamRepository.LockStaffUpdateAsync(
                unit, teamId, callerSubjectId, staffSubjectId, narrowing, now, ct);
            if (changes is not null)
                await ApplyAsync(unit, changes, ct, now);
            return change;
        });
    }

    public static async Task<TeamAccessApproval> ApproveAccessAsync(
        INocturneDbContextFactory contexts, string tokenHash, TeamAccessApproverCredential approver,
        TeamAccessGrant grant, DateTime now)
    {
        while (true)
        {
            await using var unit = await DbUnitOfWork.OpenPlatformAsync(contexts);
            var approval = await unit.CommitAsync<TeamAccessApproval?>(async ct =>
            {
                if (await TeamRepository.LockAccessApprovalAsync(unit, tokenHash, approver, grant, now, ct) is not { } written)
                    return null;
                if (written.Changes is not null)
                    await ApplyAsync(unit, written.Changes, ct, now);
                return written.Approval;
            });
            if (approval is not null)
                return approval;
        }
    }

    public static async Task<TenantRoleUpdate> EditRoleAsync(
        INocturneDbContextFactory contexts, Guid tenantId, Guid roleId, TenantRoleEdit edit, Guid? editorSubjectId, DateTime now)
    {
        while (true)
        {
            await using var unit = await DbUnitOfWork.OpenForTenantAsync(contexts, tenantId);
            var update = await unit.CommitAsync<TenantRoleUpdate?>(async ct =>
            {
                if (await TenantMembershipRepository.LockRoleEditAsync(unit, tenantId, roleId, edit, editorSubjectId, now, ct) is not { } written)
                    return null;
                if (written.Changes is not null)
                    await ApplyAsync(unit, written.Changes, ct, now);
                return written.Update;
            });
            if (update is not null)
                return update;
        }
    }

    /// <summary>The changes' writer for the startup check.</summary>
    public static Func<DbUnitOfWork, TeamReasonChanges, CancellationToken, Task> Apply(DateTime now) =>
        (unit, changes, ct) => ApplyAsync(unit, changes, ct, now);

    private static async Task<bool> ApplyAsync(DbUnitOfWork unit, TeamReasonChanges changes, CancellationToken ct, DateTime at)
    {
        foreach (var site in changes.Sites)
        {
            await unit.Context.InTenantForTransactionAsync(site, async token =>
            {
                await changes.StageReasonsAtAsync(site, at, token);
                await unit.Context.SaveChangesAsync(token);
                return true;
            }, ct);
        }

        await changes.StageAsync(ct);
        await unit.Context.SaveChangesAsync(ct);
        return true;
    }
}
