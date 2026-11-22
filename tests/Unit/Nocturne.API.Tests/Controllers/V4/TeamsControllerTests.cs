using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;
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

    private static IEnumerable<MethodInfo> Actions => typeof(TeamsController)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

    public static TheoryData<string> ActionNames => new(Actions.Select(m => m.Name));

    private static IEnumerable<AuthType> NonInteractiveCredentials =>
        Enum.GetValues<AuthType>().Where(t => t is not (AuthType.SessionCookie or AuthType.LegacyJwt or AuthType.None));

    [Fact]
    public void Every_action_is_one_of_the_ten_team_routes() =>
        Actions.Select(m => m.Name).Should().BeEquivalentTo(
            "GetMyTeams", "CreateTeam", "GetTeam", "RenameTeam", "RemoveManager", "CreateManagerInvite",
            "RevokeManagerInvite", "GetInviteInfo", "AcceptInvite", "DeleteTeam");

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

        var result = await Controller(Session()).RenameTeam(TeamId, new TeamNameRequest(new string('a', 101)), default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        CodeOf(problem).Should().Be(TeamProblemCodes.NameInvalid);
    }

    [Fact]
    public async Task Every_route_naming_a_team_the_caller_is_not_in_answers_not_found()
    {
        _teams.Setup(t => t.GetAsync(TeamId, Caller, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync((TeamDetail?)null);
        _teams.Setup(t => t.RenameAsync(TeamId, Caller, "New", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _teams.Setup(t => t.RemoveManagerAsync(TeamId, Caller, It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ManagerGuardedChange.NotFound);
        _teams.Setup(t => t.CreateInviteAsync(TeamId, Caller, It.IsAny<NewTeamInvite>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);
        _teams.Setup(t => t.RevokeInviteAsync(TeamId, Caller, It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _teams.Setup(t => t.DeleteAsync(TeamId, Caller, It.IsAny<CancellationToken>())).ReturnsAsync(TeamDeletion.NotFound);
        var controller = Controller(Session());

        IActionResult[] results =
        [
            await controller.GetTeam(TeamId, default),
            await controller.RenameTeam(TeamId, new TeamNameRequest("New"), default),
            await controller.RemoveManager(TeamId, Guid.CreateVersion7(), default),
            await controller.CreateManagerInvite(TeamId, new CreateTeamInviteRequest(null), default),
            await controller.RevokeManagerInvite(TeamId, Guid.CreateVersion7(), default),
            await controller.DeleteTeam(TeamId, default),
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
    public async Task A_refused_delete_carries_its_code()
    {
        _teams.Setup(t => t.DeleteAsync(TeamId, Caller, It.IsAny<CancellationToken>())).ReturnsAsync(TeamDeletion.Refused);

        var result = await Controller(Session()).DeleteTeam(TeamId, default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        CodeOf(problem).Should().Be(TeamProblemCodes.DeleteRefused);
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

    private static object? CodeOf(ObjectResult problem) =>
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Extensions["code"];

    private void Policy(bool restricted) =>
        _policies.Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformPolicy(RestrictTeamCreationToAdmins: restricted));

    private static AuthContext Session(bool isPlatformAdmin = false) => new()
    {
        IsAuthenticated = true, AuthType = AuthType.SessionCookie, SubjectId = Caller, IsPlatformAdmin = isPlatformAdmin,
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
