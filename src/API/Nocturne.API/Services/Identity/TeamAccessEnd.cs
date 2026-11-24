using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Ends what a team gives at its sites: a site stopping the team's access, the team being deleted, and a
/// staff member being removed or leaving. Each removes the team reasons that stand on what goes, in one
/// commit with it (<see cref="TeamReasonEnding"/>). A member whose last reason it was loses the
/// membership with what a direct removal takes (<see cref="MembershipRemoval.StageLossAsync"/>), what
/// they made stopped at once; a member another reason keeps has what they made capped at the end the
/// rest gives.
/// </summary>
/// <remarks>
/// Each site's rows are held to that site by row level security, so each site's part runs with the
/// unit pinned to it (<see cref="RlsPinningExtensions.InTenantForTransactionAsync{T}"/>) and is saved
/// before the next. What the member made is staged before their membership's own rows, as
/// <see cref="MembershipRemoval"/> orders them. Sessions of the grants revoked or capped are evicted
/// once the unit commits.
/// </remarks>
internal static class TeamAccessEnd
{
    /// <summary>Stops the team's access to <paramref name="tenantId"/>. False when it has none.</summary>
    public static async Task<bool> StopAsync(
        INocturneDbContextFactory contexts, GrantRevocationService grantRevocation, Guid tenantId, Guid teamId, DateTime now,
        CancellationToken ct)
    {
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(contexts, tenantId, ct);
        var lost = await unit.CommitAsync(
            async token => await TeamRepository.LockAccessStopAsync(unit, tenantId, teamId, token) is { } ending
                ? await EndAsync(unit, ending, grantRevocation, now, token)
                : null,
            ct: ct);
        grantRevocation.EvictSessions(lost ?? []);
        return lost is not null;
    }

    /// <summary>
    /// Deletes the team with its members, invites and access to sites, recording each site's access as
    /// stopped. <see cref="TeamDeletion.NotFound"/> when the caller is not a member,
    /// <see cref="TeamDeletion.Refused"/> when the team may not be deleted now.
    /// </summary>
    public static async Task<TeamDeletion> DeleteTeamAsync(
        INocturneDbContextFactory contexts, GrantRevocationService grantRevocation, Guid teamId, Guid callerSubjectId,
        DateTime now, CancellationToken ct)
    {
        await using var unit = await DbUnitOfWork.OpenPlatformAsync(contexts, ct);
        var (outcome, lost) = await unit.CommitAsync(
            async token =>
            {
                var (found, ending) = await TeamRepository.LockTeamDeletionAsync(unit, teamId, callerSubjectId, token);
                return (found, ending is null ? [] : await EndAsync(unit, ending, grantRevocation, now, token));
            },
            ct: ct);
        grantRevocation.EvictSessions(lost);
        return outcome;
    }

    /// <summary>
    /// Removes the staff member from the team and every site's record of them; the caller's own id means
    /// leave. False when the subject is not staff of the team, or the caller is neither a manager of it
    /// nor that staff member. Never refused for the last-manager rule.
    /// </summary>
    public static async Task<bool> RemoveStaffAsync(
        INocturneDbContextFactory contexts, GrantRevocationService grantRevocation, Guid teamId, Guid callerSubjectId,
        Guid staffSubjectId, DateTime now, CancellationToken ct)
    {
        await using var unit = await DbUnitOfWork.OpenPlatformAsync(contexts, ct);
        var lost = await unit.CommitAsync(
            async token => await TeamRepository.LockStaffRemovalAsync(unit, teamId, callerSubjectId, staffSubjectId, token) is { } ending
                ? await EndAsync(unit, ending, grantRevocation, now, token)
                : null,
            ct: ct);
        grantRevocation.EvictSessions(lost ?? []);
        return lost is not null;
    }

    private static async Task<IReadOnlyList<OAuthGrantRecord>> EndAsync(
        DbUnitOfWork unit, TeamReasonEnding ending, GrantRevocationService grantRevocation, DateTime now, CancellationToken ct)
    {
        var lost = new List<OAuthGrantRecord>();
        foreach (var site in ending.Sites)
        {
            lost.AddRange(await unit.Context.InTenantForTransactionAsync(site, async token =>
            {
                var stops = new List<(Guid SubjectId, MadeByStop Made)>();
                foreach (var subjectId in ending.EndingAt(site))
                    stops.Add((subjectId, await MadeByStop.StageAsync(unit, site, subjectId, now, token)));

                List<OAuthGrantRecord> lostHere = [.. await ending.RemoveReasonsAtAsync(site, now, token)];
                foreach (var (subjectId, made) in stops)
                    lostHere.AddRange(await MembershipRemoval.StageLossAsync(unit, made, grantRevocation, site, subjectId, capAt: now, token));

                await unit.Context.SaveChangesAsync(token);
                return lostHere;
            }, ct));
        }

        await ending.StageAsync(ct);
        await unit.Context.SaveChangesAsync(ct);
        return lost;
    }
}
