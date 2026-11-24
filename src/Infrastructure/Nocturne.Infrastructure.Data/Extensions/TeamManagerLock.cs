using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// A team's manager lock (<see cref="AdvisoryLockExtensions"/>): what every write that can leave a
/// team with no manager, or add a member to it, holds from its check to its commit.
/// </summary>
/// <remarks>
/// Two managers who each find the other before either commits would leave the team with none, and a
/// team with no manager can be run by nobody. A write that spans teams takes the locks in ascending
/// team id, so two of them cannot each hold what the other waits for.
/// </remarks>
internal static class TeamManagerLock
{
    internal const int Class = 0x4E54_4D4D;

    internal static Task LockTeamManagersAsync(this NocturneDbContext db, Guid teamId, CancellationToken ct) =>
        db.LockForTransactionAsync(Class, teamId, ct);

    /// <summary>
    /// Takes the manager lock of every team the subject belongs to, in ascending team id, and returns
    /// those teams (<see cref="MembershipLock"/> gives the order).
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    internal static async Task<IReadOnlyList<Guid>> LockTeamManagersOfSubjectAsync(
        this NocturneDbContext db, Guid subjectId, CancellationToken ct)
    {
        var teamIds = await db.TeamMembers.AsNoTracking()
            .Where(m => m.SubjectId == subjectId)
            .OrderBy(m => m.TeamId)
            .Select(m => m.TeamId)
            .ToListAsync(ct);
        foreach (var teamId in teamIds)
            await db.LockTeamManagersAsync(teamId, ct);
        return teamIds;
    }

    /// <summary>
    /// Takes the manager lock of every team the subject belongs to, then sorts them for the
    /// subject's deletion: the teams the subject is the only member of, which go with them, and the
    /// teams with other members whose last manager they are (<see cref="TeamManagerFilter.IsLastManagerAsync"/>),
    /// which refuse it.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on the context.</exception>
    internal static async Task<(IReadOnlyList<TeamEntity> SoleMemberOf, IReadOnlyList<LastManagerTeam> LastManagerOf)>
        LockTeamsOfSubjectAsync(this NocturneDbContext db, Guid subjectId, CancellationToken ct)
    {
        var teamIds = await db.LockTeamManagersOfSubjectAsync(subjectId, ct);

        var teams = await db.Teams
            .Where(t => teamIds.Contains(t.Id))
            .Select(t => new { Team = t, Others = t.Members.Any(m => m.SubjectId != subjectId) })
            .ToListAsync(ct);

        var soleMemberOf = new List<TeamEntity>();
        var lastManagerOf = new List<LastManagerTeam>();
        foreach (var team in teams.OrderBy(t => t.Team.Id))
        {
            if (!team.Others)
                soleMemberOf.Add(team.Team);
            else if (await db.IsLastManagerAsync(team.Team.Id, subjectId, ct))
                lastManagerOf.Add(new LastManagerTeam(team.Team.Id, team.Team.Name));
        }

        return (soleMemberOf, lastManagerOf);
    }
}

/// <summary>The one predicate for "the managers who run this team".</summary>
/// <remarks>
/// Holding the manager kind is not enough on its own, as <see cref="TenantOwnerFilter.PeopleOf"/> states
/// for owners: a deactivated subject cannot sign in, and a system subject is no person.
/// </remarks>
public static class TeamManagerFilter
{
    /// <summary>The team's managers who are people: active and not a system subject.</summary>
    public static IQueryable<TeamMemberEntity> ManagersWhoCount(this IQueryable<TeamMemberEntity> members, Guid teamId) =>
        members.Where(m => m.TeamId == teamId
            && m.Kind == TeamMemberKind.Manager
            && !m.Subject!.IsSystemSubject
            && m.Subject.IsActive);

    public const string LastManagerRefusal = "Cannot remove the only manager of the team, or the last manager who can sign in";

    /// <summary>
    /// Whether <paramref name="subjectId"/> manages the team and losing them would leave it with no
    /// manager at all, or, when they count, with no other manager who counts.
    /// </summary>
    /// <remarks>
    /// A manager who does not count may go while another manager remains: that removes no one who
    /// can run the team.
    /// </remarks>
    /// <param name="db">The context the caller holds the manager lock on (<see cref="TeamManagerLock"/>).</param>
    public static async Task<bool> IsLastManagerAsync(
        this NocturneDbContext db, Guid teamId, Guid subjectId, CancellationToken ct)
    {
        var managers = db.TeamMembers.AsNoTracking()
            .Where(m => m.TeamId == teamId && m.Kind == TeamMemberKind.Manager);
        if (!await managers.AnyAsync(m => m.SubjectId == subjectId, ct))
            return false;

        if (!await managers.AnyAsync(m => m.SubjectId != subjectId, ct))
            return true;

        var counting = db.TeamMembers.AsNoTracking().ManagersWhoCount(teamId);
        return await counting.AnyAsync(m => m.SubjectId == subjectId, ct)
            && !await counting.AnyAsync(m => m.SubjectId != subjectId, ct);
    }
}
