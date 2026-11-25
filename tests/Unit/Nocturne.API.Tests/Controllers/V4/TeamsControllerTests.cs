using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// <see cref="TeamsController"/>: who may call it at all, who may create a team under the operator's
/// setting, and that a team the caller is not in answers 404 on every route that names it.
/// </summary>
[Trait("Category", "Unit")]
public class TeamsControllerTests
{
    private static readonly Guid Caller = Guid.CreateVersion7();
    private static readonly Guid TeamId = Guid.CreateVersion7();

    private readonly Mock<ITeamRepository> _teams = new(MockBehavior.Strict);
    private readonly Mock<IPlatformPolicyRepository> _policies = new();
    private readonly Mock<ISubjectRepository> _subjects = new();
    private readonly Mock<ITenantMembershipRepository> _members = new();
    private readonly Mock<ITenantRoleService> _roles = new();

    public TeamsControllerTests() =>
        _subjects.Setup(s => s.GetSignInStateAsync(Caller, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubjectSignInState(IsActive: true, IsPlatformAdmin: false));

    private static IEnumerable<MethodInfo> Actions => typeof(TeamsController)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

    public static TheoryData<string> ActionNames => new(Actions.Select(m => m.Name));

    private static IEnumerable<AuthType> NonInteractiveCredentials =>
        Enum.GetValues<AuthType>().Where(t => t is not (AuthType.SessionCookie or AuthType.LegacyJwt or AuthType.None));

    [Fact]
    public void Every_action_is_one_of_the_twenty_team_routes() =>
        Actions.Select(m => m.Name).Should().BeEquivalentTo(
            "GetMyTeams", "CreateTeam", "GetTeam", "RenameTeam", "RemoveManager", "CreateManagerInvite",
            "RevokeManagerInvite", "GetInviteInfo", "AcceptInvite", "DeleteTeam",
            "CreateStaffInvite", "RevokeStaffInvite", "GetStaffInviteInfo", "AcceptStaffInvite", "UpdateStaff", "RemoveStaff",
            "CreateAccessLink", "RevokeAccessLink", "GetAccessLinkInfo", "ApproveAccessLink");

    [Theory]
    [MemberData(nameof(ActionNames))]
    public async Task Every_action_refuses_a_credential_that_is_not_the_persons_own_sign_in(string action)
    {
        foreach (var authType in NonInteractiveCredentials)
        {
            var result = await AuthorizeAsync(action, new AuthContext
            {
                IsAuthenticated = true, AuthType = authType, SubjectId = Caller,
            }, isDemo: false);

            result.Should().NotBeNull($"{action} is for a person in a browser session, not a {authType} credential");
        }
    }

    [Theory]
    [MemberData(nameof(ActionNames))]
    public async Task Every_action_refuses_the_demo_subject(string action) =>
        (await AuthorizeAsync(action, Session(), isDemo: true)).Should().BeOfType<ForbidResult>();

    [Theory]
    [MemberData(nameof(ActionNames))]
    public async Task Every_action_admits_a_persons_own_session(string action) =>
        (await AuthorizeAsync(action, Session(), isDemo: false)).Should().BeNull();

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public async Task Can_create_follows_the_operator_setting_and_the_platform_admin_flag(
        bool restricted, bool isPlatformAdmin, bool canCreate)
    {
        Policy(restricted);
        _teams.Setup(t => t.GetTeamsOfAsync(Caller, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var result = await Controller(Session(isPlatformAdmin)).GetMyTeams(default);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(new MyTeamsResponse([], canCreate));
    }

    [Fact]
    public async Task A_restricted_site_refuses_creation_to_a_person_who_is_not_a_platform_admin_and_says_why()
    {
        Policy(restricted: true);

        var result = await Controller(Session()).CreateTeam(new TeamNameRequest("Camp"), default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(TeamsController.CreationRestrictedDetail);
        CodeOf(problem).Should().Be(TeamProblemCodes.CreationRestricted);
        _teams.Verify(t => t.CreateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_restricted_site_lets_a_platform_admin_create_a_team_with_its_name_trimmed()
    {
        Policy(restricted: true);
        _teams.Setup(t => t.CreateAsync(Caller, "Camp", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TeamSummary(TeamId, "Camp", TeamMemberKind.Manager));

        var result = await Controller(Session(isPlatformAdmin: true)).CreateTeam(new TeamNameRequest("  Camp "), default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status201Created);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_name_is_refused(string name)
    {
        Policy(restricted: false);

        var result = await Controller(Session()).CreateTeam(new TeamNameRequest(name), default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        CodeOf(problem).Should().Be(TeamProblemCodes.NameInvalid);
    }

    [Fact]
    public async Task A_name_over_one_hundred_characters_is_refused()
    {
        Policy(restricted: false);

        var result = await Controller(Session()).RenameTeam(TeamId, new TeamNameRequest(new string('a', 101)), Announcer(), default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        CodeOf(problem).Should().Be(TeamProblemCodes.NameInvalid);
    }

    [Fact]
    public async Task Every_route_naming_a_team_the_caller_is_not_in_answers_not_found()
    {
        _teams.Setup(t => t.GetAsync(TeamId, Caller, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync((TeamDetail?)null);
        _teams.Setup(t => t.RenameAsync(TeamId, Caller, "New", It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _teams.Setup(t => t.RemoveManagerAsync(TeamId, Caller, It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ManagerGuardedChange.NotFound);
        _teams.Setup(t => t.CreateInviteAsync(TeamId, Caller, It.IsAny<NewTeamInvite>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);
        _teams.Setup(t => t.RevokeInviteAsync(TeamId, Caller, It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _teams.Setup(t => t.CreateAccessLinkAsync(TeamId, Caller, It.IsAny<NewTeamAccessLink>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);
        _teams.Setup(t => t.RevokeAccessLinkAsync(TeamId, Caller, It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _teams.Setup(t => t.CreateStaffInviteAsync(TeamId, Caller, It.IsAny<NewTeamStaffInvite>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);
        _teams.Setup(t => t.RevokeStaffInviteAsync(TeamId, Caller, It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var controller = Controller(Session());

        IActionResult[] results =
        [
            await controller.GetTeam(TeamId, default),
            await controller.RenameTeam(TeamId, new TeamNameRequest("New"), Announcer(), default),
            await controller.RemoveManager(TeamId, Guid.CreateVersion7(), default),
            await controller.CreateManagerInvite(TeamId, new CreateTeamInviteRequest(null), default),
            await controller.RevokeManagerInvite(TeamId, Guid.CreateVersion7(), default),
            await controller.CreateAccessLink(TeamId, new CreateTeamAccessLinkRequest(null, RoleSeeds.Caretaker, false, null), default),
            await controller.RevokeAccessLink(TeamId, Guid.CreateVersion7(), default),
            await controller.CreateStaffInvite(TeamId, new CreateTeamStaffInviteRequest(null, null, false, null), default),
            await controller.RevokeStaffInvite(TeamId, Guid.CreateVersion7(), default),
        ];

        results.Should().AllBeOfType<NotFoundResult>("a 403 would tell a stranger the team exists");
    }

    [Fact]
    public async Task Removing_the_last_manager_is_refused_with_the_last_manager_detail()
    {
        _teams.Setup(t => t.RemoveManagerAsync(TeamId, Caller, Caller, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ManagerGuardedChange.LastManager);

        var result = await Controller(Session()).RemoveManager(TeamId, Caller, default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(TeamManagerFilter.LastManagerRefusal);
        CodeOf(problem).Should().Be(TeamProblemCodes.LastManager);
    }

    [Theory]
    [InlineData(TeamInviteRefusal.Expired, "This invite has expired.", TeamProblemCodes.Expired)]
    [InlineData(TeamInviteRefusal.Revoked, "This invite has been revoked.", TeamProblemCodes.Revoked)]
    [InlineData(TeamInviteRefusal.UsedUp, "This invite has reached its maximum uses.", TeamProblemCodes.UsedUp)]
    [InlineData(TeamInviteRefusal.NoLongerValid, "This invite is no longer valid.", TeamProblemCodes.NoLongerValid)]
    [InlineData(TeamInviteRefusal.AlreadyMember, "You are already a member of this team.", TeamProblemCodes.AlreadyMember)]
    public async Task An_invite_that_cannot_be_accepted_states_why(TeamInviteRefusal refusal, string detail, string code)
    {
        _teams.Setup(t => t.AcceptInviteAsync(It.IsAny<string>(), Caller, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TeamInviteAcceptance(TeamId, refusal));

        var result = await Controller(Session()).AcceptInvite("token", default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(detail);
        CodeOf(problem).Should().Be(code);
    }

    [Theory]
    [InlineData(TeamInviteRefusal.Expired, TeamProblemCodes.Expired)]
    [InlineData(TeamInviteRefusal.Revoked, TeamProblemCodes.Revoked)]
    [InlineData(TeamInviteRefusal.UsedUp, TeamProblemCodes.UsedUp)]
    [InlineData(TeamInviteRefusal.NoLongerValid, TeamProblemCodes.NoLongerValid)]
    public async Task Looking_up_an_invite_that_cannot_be_accepted_carries_the_code(TeamInviteRefusal refusal, string code)
    {
        _teams.Setup(t => t.GetInviteInfoAsync(It.IsAny<string>(), Caller, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TeamInviteInfo(TeamId, "Camp", "Sam", TeamMemberKind.Manager, refusal));

        var result = await Controller(Session()).GetInviteInfo("token", default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        CodeOf(problem).Should().Be(code);
    }

    [Fact]
    public async Task Looking_up_an_invite_says_what_accepting_makes_the_caller()
    {
        _teams.Setup(t => t.GetInviteInfoAsync(It.IsAny<string>(), Caller, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TeamInviteInfo(TeamId, "Camp", "Sam", TeamMemberKind.Manager, null));

        var result = await Controller(Session()).GetInviteInfo("token", default);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should()
            .Be(new TeamInviteDetails(TeamId, "Camp", "Sam", TeamMemberKind.Manager, false));
    }

    [Fact]
    public async Task An_inactive_subject_is_refused_every_action_that_mints_a_link_and_nothing_is_stored()
    {
        _subjects.Setup(s => s.GetSignInStateAsync(Caller, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubjectSignInState(IsActive: false, IsPlatformAdmin: false));
        var controller = Controller(Session());

        IActionResult[] results =
        [
            await controller.CreateManagerInvite(TeamId, new CreateTeamInviteRequest(null), default),
            await controller.CreateAccessLink(TeamId, new CreateTeamAccessLinkRequest(null, RoleSeeds.Caretaker, false, null), default),
            await controller.ApproveAccessLink(
                "token", new ApproveTeamAccessRequest(Guid.CreateVersion7(), [Guid.CreateVersion7()], false, null, null), Access(), default),
        ];

        results.Should().AllBeOfType<ChallengeResult>();
        _teams.Invocations.Should().BeEmpty();
        _members.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_subject_with_no_row_is_refused_as_inactive_and_nothing_is_stored()
    {
        _subjects.Setup(s => s.GetSignInStateAsync(Caller, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SubjectSignInState?)null);

        var result = await Controller(Session()).CreateManagerInvite(TeamId, new CreateTeamInviteRequest(null), default);

        result.Should().BeOfType<ChallengeResult>();
        _teams.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task An_active_subject_is_not_refused_by_the_state_read()
    {
        _subjects.Setup(s => s.GetSignInStateAsync(Caller, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubjectSignInState(IsActive: true, IsPlatformAdmin: false));
        _teams.Setup(t => t.CreateInviteAsync(TeamId, Caller, It.IsAny<NewTeamInvite>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.CreateVersion7());

        var result = await Controller(Session()).CreateManagerInvite(TeamId, new CreateTeamInviteRequest(null), default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status201Created);
    }

    [Theory]
    [InlineData(0, 1, null, TeamProblemCodes.ExpiryInvalid)]
    [InlineData(7, 0, null, TeamProblemCodes.UsesInvalid)]
    [InlineData(7, 1, 256, TeamProblemCodes.LabelInvalid)]
    public async Task A_refused_invite_request_carries_the_code_of_what_is_wrong(
        int expiresInDays, int maxUses, int? labelLength, string code)
    {
        var label = labelLength is { } length ? new string('a', length) : null;

        var result = await Controller(Session()).CreateManagerInvite(
            TeamId, new CreateTeamInviteRequest(label, expiresInDays, maxUses), default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        CodeOf(problem).Should().Be(code);
    }

    [Fact]
    public async Task A_staff_token_nobody_holds_is_not_found_when_looked_up()
    {
        _teams.Setup(t => t.GetStaffInviteInfoAsync(It.IsAny<string>(), Caller, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TeamStaffInviteInfo?)null);

        (await Controller(Session()).GetStaffInviteInfo("token", default)).Should().BeOfType<NotFoundResult>();
    }

    [Theory]
    [InlineData(TeamInviteRefusal.Expired, TeamProblemCodes.Expired)]
    [InlineData(TeamInviteRefusal.Revoked, TeamProblemCodes.Revoked)]
    [InlineData(TeamInviteRefusal.UsedUp, TeamProblemCodes.UsedUp)]
    [InlineData(TeamInviteRefusal.NoLongerValid, TeamProblemCodes.NoLongerValid)]
    [InlineData(TeamInviteRefusal.StaffEndPassed, TeamProblemCodes.StaffInviteEndPassed)]
    public async Task Looking_up_a_staff_invite_that_cannot_be_accepted_carries_the_code(TeamInviteRefusal refusal, string code)
    {
        _teams.Setup(t => t.GetStaffInviteInfoAsync(It.IsAny<string>(), Caller, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TeamStaffInviteInfo(TeamId, "Camp", "Sam", new TeamStaffNarrowing(null, false, null), refusal));

        var result = await Controller(Session()).GetStaffInviteInfo("token", default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        CodeOf(problem).Should().Be(code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Looking_up_a_staff_invite_says_what_narrows_the_staff_member_whether_the_caller_is_a_member_and_the_role_names(bool member)
    {
        var narrowing = new TeamStaffNarrowing(RoleSeeds.Viewer, true, DateTime.UtcNow.AddDays(3));
        _teams.Setup(t => t.GetStaffInviteInfoAsync(It.IsAny<string>(), Caller, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TeamStaffInviteInfo(TeamId, "Camp", "Sam", narrowing, member ? TeamInviteRefusal.AlreadyMember : null));

        var result = await Controller(Session()).GetStaffInviteInfo("token", default);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should()
            .Be(new TeamStaffInviteDetails(TeamId, "Camp", "Sam", narrowing, member, TeamSuggestableRoles.All));
    }

    [Fact]
    public async Task A_new_staff_invite_returns_its_token_once_stores_only_its_hash_and_carries_what_it_gives()
    {
        NewTeamStaffInvite? stored = null;
        var inviteId = Guid.CreateVersion7();
        _teams.Setup(t => t.CreateStaffInviteAsync(TeamId, Caller, It.IsAny<NewTeamStaffInvite>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, NewTeamStaffInvite, CancellationToken>((_, _, invite, _) => stored = invite)
            .ReturnsAsync(inviteId);
        var end = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var result = await Controller(Session()).CreateStaffInvite(
            TeamId, new CreateTeamStaffInviteRequest("Night shift", RoleSeeds.Clinician, true, end), default);

        var created = result.Should().BeOfType<ObjectResult>().Which.Value.Should().BeOfType<TeamStaffInviteCreated>().Subject;
        created.Token.Should().Be("minted-token");
        created.InviteUrl.Should().EndWith($"{TeamsController.StaffJoinPath}?token=minted-token");
        stored!.TokenHash.Should().NotContain("minted-token").And.HaveLength(64);
        stored.Label.Should().Be("Night shift");
        stored.Narrowing.Should().Be(new TeamStaffNarrowing(RoleSeeds.Clinician, true, end.UtcDateTime));
    }

    [Theory]
    [InlineData(RoleSeeds.Owner, null, 7, TeamProblemCodes.StaffCeilingInvalid)]
    [InlineData(RoleSeeds.Admin, null, 7, TeamProblemCodes.StaffCeilingInvalid)]
    [InlineData(RoleSeeds.Denied, null, 7, TeamProblemCodes.StaffCeilingInvalid)]
    [InlineData("no-such-role", null, 7, TeamProblemCodes.StaffCeilingInvalid)]
    [InlineData(RoleSeeds.Viewer, -1, 7, TeamProblemCodes.StaffEndInvalid)]
    [InlineData(RoleSeeds.Viewer, null, 0, TeamProblemCodes.ExpiryInvalid)]
    public async Task A_refused_staff_invite_request_carries_the_code_of_what_is_wrong(
        string? ceiling, int? endInDays, int expiresInDays, string code)
    {
        DateTimeOffset? end = endInDays is { } days ? DateTimeOffset.UtcNow.AddDays(days) : null;

        var result = await Controller(Session()).CreateStaffInvite(
            TeamId, new CreateTeamStaffInviteRequest(null, ceiling, false, end, expiresInDays), default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        CodeOf(problem).Should().Be(code);
    }

    [Theory]
    [InlineData(RoleSeeds.Owner)]
    [InlineData("no-such-role")]
    public async Task A_staff_edit_with_a_ceiling_staff_cannot_have_is_refused_with_its_code(string ceiling)
    {
        var result = await Controller(Session()).UpdateStaff(
            TeamId, Guid.CreateVersion7(), new UpdateTeamStaffRequest(ceiling, false, null), Access(), default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        CodeOf(problem).Should().Be(TeamProblemCodes.StaffCeilingInvalid);
    }

    [Fact]
    public async Task A_new_invite_returns_its_token_once_and_stores_only_its_hash()
    {
        NewTeamInvite? stored = null;
        var inviteId = Guid.CreateVersion7();
        _teams.Setup(t => t.CreateInviteAsync(TeamId, Caller, It.IsAny<NewTeamInvite>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, NewTeamInvite, CancellationToken>((_, _, invite, _) => stored = invite)
            .ReturnsAsync(inviteId);

        var result = await Controller(Session()).CreateManagerInvite(TeamId, new CreateTeamInviteRequest("Night staff"), default);

        var created = result.Should().BeOfType<ObjectResult>().Which.Value.Should().BeOfType<TeamInviteCreated>().Subject;
        created.Token.Should().Be("minted-token");
        created.InviteUrl.Should().EndWith($"{TeamsController.JoinPath}?token=minted-token");
        stored!.TokenHash.Should().NotContain("minted-token").And.HaveLength(64);
        stored.Label.Should().Be("Night staff");
    }

    [Theory]
    [InlineData(RoleSeeds.Owner, 7, null, TeamProblemCodes.AccessRoleInvalid)]
    [InlineData(RoleSeeds.Admin, 7, null, TeamProblemCodes.AccessRoleInvalid)]
    [InlineData(RoleSeeds.Denied, 7, null, TeamProblemCodes.AccessRoleInvalid)]
    [InlineData("no-such-role", 7, null, TeamProblemCodes.AccessRoleInvalid)]
    [InlineData(RoleSeeds.Caretaker, 0, null, TeamProblemCodes.ExpiryInvalid)]
    [InlineData(RoleSeeds.Caretaker, 7, -1, TeamProblemCodes.AccessEndInvalid)]
    public async Task A_refused_access_link_request_carries_the_code_of_what_is_wrong(
        string roleSlug, int expiresInDays, int? endInDays, string code)
    {
        var end = endInDays is { } days ? DateTimeOffset.UtcNow.AddDays(days) : (DateTimeOffset?)null;

        var result = await Controller(Session()).CreateAccessLink(
            TeamId, new CreateTeamAccessLinkRequest(null, roleSlug, false, end, expiresInDays), default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        CodeOf(problem).Should().Be(code);
    }

    [Fact]
    public async Task A_new_access_link_returns_its_token_once_and_stores_its_hash_and_suggestion()
    {
        NewTeamAccessLink? stored = null;
        var end = DateTimeOffset.UtcNow.AddDays(30);
        _teams.Setup(t => t.CreateAccessLinkAsync(TeamId, Caller, It.IsAny<NewTeamAccessLink>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, NewTeamAccessLink, CancellationToken>((_, _, link, _) => stored = link)
            .ReturnsAsync(Guid.CreateVersion7());

        var result = await Controller(Session()).CreateAccessLink(
            TeamId, new CreateTeamAccessLinkRequest(" Clinic ", RoleSeeds.Viewer, true, end), default);

        var created = result.Should().BeOfType<ObjectResult>().Which.Value.Should().BeOfType<TeamAccessLinkCreated>().Subject;
        created.Token.Should().Be("minted-token");
        created.LinkUrl.Should().EndWith($"{TeamsController.AccessPath}?token=minted-token");
        stored!.TokenHash.Should().NotContain("minted-token").And.HaveLength(64);
        stored.Label.Should().Be("Clinic");
        stored.Suggestion.Should().Be(new TeamAccessSuggestion(RoleSeeds.Viewer, true, stored.Suggestion.AccessEndsAt));
        stored.Suggestion.AccessEndsAt.Should().BeCloseTo(end.UtcDateTime, TimeSpan.FromMilliseconds(1));
    }

    public static TheoryData<TeamAccessRefusal, int, string?> AccessRefusals => new()
    {
        { TeamAccessRefusal.NotFound, StatusCodes.Status404NotFound, null },
        { TeamAccessRefusal.Expired, StatusCodes.Status400BadRequest, TeamProblemCodes.Expired },
        { TeamAccessRefusal.Revoked, StatusCodes.Status400BadRequest, TeamProblemCodes.Revoked },
        { TeamAccessRefusal.UsedUp, StatusCodes.Status400BadRequest, TeamProblemCodes.UsedUp },
        { TeamAccessRefusal.NoLongerValid, StatusCodes.Status400BadRequest, TeamProblemCodes.NoLongerValid },
        { TeamAccessRefusal.AlreadyGiven, StatusCodes.Status400BadRequest, TeamProblemCodes.AccessAlreadyGiven },
        { TeamAccessRefusal.NotOwner, StatusCodes.Status403Forbidden, TeamProblemCodes.AccessNotOwner },
        { TeamAccessRefusal.NoRoles, StatusCodes.Status400BadRequest, TeamProblemCodes.AccessRolesRequired },
        { TeamAccessRefusal.RoleNotInTenant, StatusCodes.Status400BadRequest, TeamProblemCodes.AccessRoleNotInSite },
        { TeamAccessRefusal.RoleAdministrative, StatusCodes.Status400BadRequest, TeamProblemCodes.AccessRoleAdministrative },
        { TeamAccessRefusal.RoleWiderThanLink, StatusCodes.Status400BadRequest, TeamProblemCodes.AccessRoleWiderThanLink },
        { TeamAccessRefusal.RoleWiderThanApprover, StatusCodes.Status403Forbidden, TeamProblemCodes.AccessRoleWiderThanApprover },
        { TeamAccessRefusal.LimitRequired, StatusCodes.Status400BadRequest, TeamProblemCodes.AccessLimitRequired },
        { TeamAccessRefusal.EndLaterThanLink, StatusCodes.Status400BadRequest, TeamProblemCodes.AccessEndLaterThanLink },
        { TeamAccessRefusal.EndOutlastsApprover, StatusCodes.Status403Forbidden, TeamProblemCodes.AccessEndOutlastsApprover },
        { TeamAccessRefusal.EndInPast, StatusCodes.Status400BadRequest, TeamProblemCodes.AccessEndInvalid },
        { TeamAccessRefusal.SuggestedEndPassed, StatusCodes.Status400BadRequest, TeamProblemCodes.AccessLinkEndPassed },
    };

    [Fact]
    public void Every_access_refusal_has_a_status_and_a_code() =>
        AccessRefusals.Select(row => (TeamAccessRefusal)row[0]).Should().BeEquivalentTo(Enum.GetValues<TeamAccessRefusal>());

    [Theory]
    [MemberData(nameof(AccessRefusals))]
    public async Task An_approval_that_is_refused_answers_with_its_status_and_code(TeamAccessRefusal refusal, int status, string? code)
    {
        var site = Guid.CreateVersion7();
        _teams.Setup(t => t.GetAccessLinkInfoAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TeamAccessLinkInfo(
                Guid.CreateVersion7(), TeamId, "Clinic", "Sam", new TeamAccessSuggestion(RoleSeeds.Caretaker, false, null),
                DateTime.UtcNow.AddDays(1), refusal));

        var result = await Controller(Session()).ApproveAccessLink(
            "token", new ApproveTeamAccessRequest(site, [Guid.CreateVersion7()], false, null, null), Access(), default);

        if (code is null)
        {
            result.Should().BeOfType<NotFoundResult>();
            return;
        }

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(status);
        CodeOf(problem).Should().Be(code);
    }

    [Theory]
    [InlineData(TeamAccessRefusal.Expired, TeamProblemCodes.Expired)]
    [InlineData(TeamAccessRefusal.Revoked, TeamProblemCodes.Revoked)]
    [InlineData(TeamAccessRefusal.UsedUp, TeamProblemCodes.UsedUp)]
    [InlineData(TeamAccessRefusal.NoLongerValid, TeamProblemCodes.NoLongerValid)]
    public async Task Looking_up_an_access_link_that_cannot_be_used_carries_the_code(TeamAccessRefusal refusal, string code)
    {
        _teams.Setup(t => t.GetAccessLinkInfoAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TeamAccessLinkInfo(
                Guid.CreateVersion7(), TeamId, "Clinic", "Sam", new TeamAccessSuggestion(RoleSeeds.Caretaker, false, null),
                DateTime.UtcNow, refusal));

        var result = await Controller(Session()).GetAccessLinkInfo("token", Access(), default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        CodeOf(problem).Should().Be(code);
    }

    [Fact]
    public void The_link_a_person_opens_carries_no_manager_and_no_other_persons_sites()
    {
        typeof(TeamAccessLinkDetails).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
            "TeamId", "TeamName", "CreatedByName", "SuggestedRoleSlug", "SuggestedRolePermissions",
            "SuggestedLimitTo24Hours", "SuggestedAccessEndsAt", "ExpiresAt", "Sites");
        typeof(TeamAccessSiteOption).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
            "TenantId", "DisplayName", "AlreadyGiven", "Roles", "DefaultRoleId", "LimitForced", "LatestEnd");
        typeof(TeamSiteAccess).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
            ["TenantId", "DisplayName", "RoleNames", "LimitTo24Hours", "AccessEndsAt", "HasEnded", "GivenAt",
                "TeamManagesStaff", "PendingStaffCount", "DeclinedStaffCount"],
            "a team learns a site's name, what it was given and how many of its staff wait or were declined, never who approved, who decided or who is in it");
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public async Task The_limit_an_approver_must_give_comes_from_their_credential_and_not_from_the_hosts_tenant(
        bool credentialLimited, bool hostMembershipLimited, bool limitForced)
    {
        var site = Guid.CreateVersion7();
        _teams.Setup(t => t.GetAccessLinkInfoAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TeamAccessLinkInfo(
                Guid.CreateVersion7(), TeamId, "Clinic", "Sam", new TeamAccessSuggestion(RoleSeeds.Caretaker, false, null),
                DateTime.UtcNow.AddDays(1), null));
        _teams.Setup(t => t.ListSitesWithAccessAsync(TeamId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid>());
        _members.Setup(m => m.ListMembershipsOfSubjectAsync(Caller, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([OwnerOf(site)]);
        _roles.Setup(r => r.GetRolesAsync(site, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var onAnotherSitesHost = Session();
        onAnotherSitesHost.LimitTo24Hours = credentialLimited;
        onAnotherSitesHost.FoldMembershipLimit(credentialLimited || hostMembershipLimited);

        var result = await Controller(onAnotherSitesHost).GetAccessLinkInfo("token", Access(), default);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<TeamAccessLinkDetails>()
            .Which.Sites.Should().ContainSingle().Which.LimitForced.Should().Be(limitForced);
    }

    [Theory]
    [InlineData("Old", true)]
    [InlineData("New", false)]
    public async Task A_rename_is_announced_only_when_the_name_changed(string oldName, bool announced)
    {
        _teams.Setup(t => t.RenameAsync(TeamId, Caller, "New", It.IsAny<CancellationToken>())).ReturnsAsync(oldName);
        _teams.Setup(t => t.ListAccessApproversAsync(TeamId, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        (await Controller(Session()).RenameTeam(TeamId, new TeamNameRequest("New"), Announcer(), default))
            .Should().BeOfType<NoContentResult>();

        _teams.Verify(t => t.ListAccessApproversAsync(TeamId, It.IsAny<CancellationToken>()), announced ? Times.Once() : Times.Never());
    }

    private static SubjectMembership OwnerOf(Guid site) => new(
        new TenantRecord(site, "site", "Site", true, false, false, false, null, null, null, null, null,
            null, null, null, null, null, null, DateTime.UtcNow, DateTime.UtcNow, null),
        new TenantMemberRecord(
            Guid.CreateVersion7(), site, Caller, "Sam", null, true, false, false, false, false,
            [new TenantMemberRoleRecord(Guid.CreateVersion7(), "Owner", RoleSeeds.Owner, [Scope.FullAccess])],
            null, null, null, false, null, null, null, DateTime.UtcNow, []));

    private static SiteNotices Sites() => new(Mock.Of<ITenantRepository>(), new ServiceCollection().BuildServiceProvider());

    private TeamRenameAnnouncer Announcer() => new(
        _teams.Object, _members.Object, Sites(), TimeProvider.System, NullLogger<TeamRenameAnnouncer>.Instance);

    private TeamAccessService Access() => new(
        _teams.Object, _members.Object, _roles.Object, TimeProvider.System, Mock.Of<INocturneDbContextFactory>(),
        new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
        new TeamStaffAnnouncer(_teams.Object, _members.Object, Sites(), TimeProvider.System, NullLogger<TeamStaffAnnouncer>.Instance));

    private static object? CodeOf(ObjectResult problem) =>
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Extensions["code"];

    private void Policy(bool restricted) =>
        _policies.Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformPolicy(RestrictTeamCreationToAdmins: restricted));

    private static AuthContext Session(bool isPlatformAdmin = false) => new()
    {
        IsAuthenticated = true, AuthType = AuthType.SessionCookie, SubjectId = Caller, IsPlatformAdmin = isPlatformAdmin,
        SessionId = LiveSession.Id,
    };

    private TeamsController Controller(AuthContext auth)
    {
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.GenerateRefreshToken()).Returns("minted-token");
        var httpContext = HttpContext(auth);
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("example.test");
        return new TeamsController(_teams.Object, _policies.Object, jwt.Object, TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private DefaultHttpContext HttpContext(AuthContext auth)
    {
        var services = new ServiceCollection();
        services.AddMvcCore();
        services.AddSingleton(_subjects.Object);
        services.AddLiveSession();
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        httpContext.Items["AuthContext"] = auth;
        return httpContext;
    }

    /// <summary>Runs the action's authorization filters, class and method, as MVC would; null when all admit.</summary>
    private async Task<IActionResult?> AuthorizeAsync(string action, AuthContext auth, bool isDemo)
    {
        _subjects.Setup(s => s.IsDemoSubjectAsync(Caller, It.IsAny<CancellationToken>())).ReturnsAsync(isDemo);
        var method = typeof(TeamsController).GetMethod(action)!;
        var filters = typeof(TeamsController).GetCustomAttributes(true).Concat(method.GetCustomAttributes(true)).ToList();
        var context = new AuthorizationFilterContext(
            new ActionContext(HttpContext(auth), new RouteData(), new ActionDescriptor()), []);

        foreach (var filter in filters)
        {
            if (filter is IAuthorizationFilter sync)
                sync.OnAuthorization(context);
            else if (filter is IAsyncAuthorizationFilter async)
                await async.OnAuthorizationAsync(context);
            if (context.Result is not null)
                return context.Result;
        }

        return null;
    }
}
