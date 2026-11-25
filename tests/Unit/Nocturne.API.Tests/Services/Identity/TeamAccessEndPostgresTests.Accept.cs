using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// What the staff-invite accept answers for a refusal the join reads under its locks: the controller
/// over the real join, since the refusals come from the rows.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    private TeamsController StaffInviteController(Guid subject) => new(
        Teams(subject), Mock.Of<IPlatformPolicyRepository>(), Mock.Of<IJwtService>(), TimeProvider.System)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().AddMvcCore().Services.BuildServiceProvider(),
                Items = { ["AuthContext"] = new AuthContext { IsAuthenticated = true, AuthType = AuthType.SessionCookie, SubjectId = subject } },
            },
        },
    };

    /// <summary>A staff invite to <paramref name="team"/> whose stored hash is that of the token returned, as a link carries it.</summary>
    private async Task<string> StaffInviteLinkAsync(Team team, TeamStaffNarrowing narrowing)
    {
        var token = Guid.NewGuid().ToString("N");
        (await Teams(team.Manager).CreateStaffInviteAsync(
            team.Id, team.Manager, new NewTeamStaffInvite(HashUtils.Sha256Hex(token), null, Now.AddDays(1), null, narrowing), Ct)).Should().NotBeNull();
        return token;
    }

    [Fact]
    public async Task Accepting_a_staff_invite_whose_end_has_passed_answers_that_it_has()
    {
        var team = await TeamAsync("Clinic");
        var staff = await NewSubjectAsync("staff");
        var token = await StaffInviteLinkAsync(team, new TeamStaffNarrowing(null, false, Now.AddHours(1)));
        await NpgsqlTestCommands.ExecuteAsync(
            database.MigratorConnectionString, $"UPDATE team_staff_invites SET staff_ends_at = '{Sql(Passed)}' WHERE token_hash = '{HashUtils.Sha256Hex(token)}'");

        var result = await StaffInviteController(staff).AcceptStaffInvite(token, Service(Guid.Empty, staff), Ct);

        result.Should().BeAssignableTo<ObjectResult>().Which.Value.Should().BeOfType<ProblemDetails>()
            .Which.Extensions["code"].Should().Be(TeamProblemCodes.StaffInviteEndPassed);
        (await Teams().GetTeamsOfAsync(staff, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Accepting_a_staff_invite_after_the_account_is_deleted_answers_not_found()
    {
        var team = await TeamAsync("Clinic");
        var staff = await NewSubjectAsync("staff");
        var token = await StaffInviteLinkAsync(team, new TeamStaffNarrowing(null, false, null));
        await Deletion(staff).DeleteAsync(staff, false, Ct);

        var result = await StaffInviteController(staff).AcceptStaffInvite(token, Service(Guid.Empty, staff), Ct);

        result.Should().BeOfType<NotFoundResult>();
        (await ReadAsync($"SELECT count(*)::text FROM team_members WHERE subject_id = '{staff}'")).Should().Equal("0");
    }
}
