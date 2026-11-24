using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// Runs the team writes that end team reasons (<see cref="TeamRepository.LockAccessStopAsync"/> and its
/// siblings) on a unit of their own, for tests in which no one holds a team reason. Ending one stops what
/// the member made, which only the API does, so a write that meets a team reason fails here.
/// </summary>
internal static class TeamEnds
{
    public static async Task<bool> StopAccessAsync(INocturneDbContextFactory contexts, Guid tenantId, Guid teamId)
    {
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(contexts, tenantId);
        return await unit.CommitAsync(async ct =>
            await TeamRepository.LockAccessStopAsync(unit, tenantId, teamId, ct) is { } ending && await WriteAsync(unit, ending, ct));
    }

    public static async Task<TeamDeletion> DeleteAsync(INocturneDbContextFactory contexts, Guid teamId, Guid callerSubjectId)
    {
        await using var unit = await DbUnitOfWork.OpenPlatformAsync(contexts);
        return await unit.CommitAsync(async ct =>
        {
            var (outcome, ending) = await TeamRepository.LockTeamDeletionAsync(unit, teamId, callerSubjectId, ct);
            if (ending is not null)
                await WriteAsync(unit, ending, ct);
            return outcome;
        });
    }

    public static async Task<bool> RemoveStaffAsync(
        INocturneDbContextFactory contexts, Guid teamId, Guid callerSubjectId, Guid staffSubjectId)
    {
        await using var unit = await DbUnitOfWork.OpenPlatformAsync(contexts);
        return await unit.CommitAsync(async ct =>
            await TeamRepository.LockStaffRemovalAsync(unit, teamId, callerSubjectId, staffSubjectId, ct) is { } ending
            && await WriteAsync(unit, ending, ct));
    }

    private static async Task<bool> WriteAsync(DbUnitOfWork unit, TeamReasonEnding ending, CancellationToken ct)
    {
        if (ending.Sites.Count > 0)
            throw new InvalidOperationException("A team reason stands on what this write removes; end it through the API.");

        await ending.StageAsync(ct);
        await unit.Context.SaveChangesAsync(ct);
        return true;
    }
}
