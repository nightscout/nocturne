using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// What the teams, saved views and flag rules routes open to a demo subject. The clinic persona is staff of a real
/// team, so it reads all three and, while the demo allows it, writes its own views and rules. Every route that runs a
/// team, invites to one, or links a site to one stays refused, since a demo session is anyone's.
/// </summary>
public class DemoSubjectTeamSurfaceTests
{
    private static readonly Type[] Controllers =
        [typeof(TeamsController), typeof(PeopleViewsController), typeof(FlagRulesController)];

    /// <summary>Every action of <see cref="Controllers"/>, with what it opens to a demo subject; null for nothing.</summary>
    private static readonly IReadOnlyDictionary<string, DemoSubjectOpening?> Expected =
        new Dictionary<string, DemoSubjectOpening?>(StringComparer.Ordinal)
        {
            ["TeamsController.GetMyTeams"] = DemoSubjectOpening.Read,
            ["TeamsController.GetTeam"] = DemoSubjectOpening.Read,
            ["PeopleViewsController.GetPeopleViews"] = DemoSubjectOpening.Read,
            ["FlagRulesController.GetFlagRules"] = DemoSubjectOpening.Read,
            ["FlagRulesController.GetFlagRuleFacts"] = DemoSubjectOpening.Read,
            ["FlagRulesController.GetTeamFlagRules"] = DemoSubjectOpening.Read,

            ["PeopleViewsController.CreatePeopleView"] = DemoSubjectOpening.StaffWrite,
            ["PeopleViewsController.UpdatePeopleView"] = DemoSubjectOpening.StaffWrite,
            ["PeopleViewsController.DeletePeopleView"] = DemoSubjectOpening.StaffWrite,
            ["PeopleViewsController.ReorderPeopleViews"] = DemoSubjectOpening.StaffWrite,
            ["FlagRulesController.CreateFlagRule"] = DemoSubjectOpening.StaffWrite,
            ["FlagRulesController.UpdateFlagRule"] = DemoSubjectOpening.StaffWrite,
            ["FlagRulesController.DeleteFlagRule"] = DemoSubjectOpening.StaffWrite,

            ["TeamsController.CreateTeam"] = null,

            ["TeamsController.CreateManagerInvite"] = null,
            ["TeamsController.RevokeManagerInvite"] = null,
            ["TeamsController.GetInviteInfo"] = null,
            ["TeamsController.AcceptInvite"] = null,

            ["TeamsController.CreateStaffInvite"] = null,
            ["TeamsController.RevokeStaffInvite"] = null,
            ["TeamsController.GetStaffInviteInfo"] = null,
            ["TeamsController.AcceptStaffInvite"] = null,

            // The token reads stay with their flows: a demo subject can follow neither through, and it owns the demo
            // sites an access link would ask it to give.
            ["TeamsController.CreateAccessLink"] = null,
            ["TeamsController.RevokeAccessLink"] = null,
            ["TeamsController.GetAccessLinkInfo"] = null,
            ["TeamsController.ApproveAccessLink"] = null,

            ["TeamsController.RenameTeam"] = null,
            ["TeamsController.RemoveManager"] = null,
            ["TeamsController.UpdateStaff"] = null,
            ["TeamsController.RemoveStaff"] = null,
            ["TeamsController.CreateGroup"] = null,
            ["TeamsController.RenameGroup"] = null,
            ["TeamsController.DeleteGroup"] = null,
            ["TeamsController.AddGroupPerson"] = null,
            ["TeamsController.RemoveGroupPerson"] = null,
            ["TeamsController.AddGroupStaff"] = null,
            ["TeamsController.RemoveGroupStaff"] = null,
            ["TeamsController.SetStaffReach"] = null,
            ["TeamsController.DeleteTeam"] = null,
            ["FlagRulesController.CreateTeamFlagRule"] = null,
            ["FlagRulesController.UpdateTeamFlagRule"] = null,
            ["FlagRulesController.DeleteTeamFlagRule"] = null,
        };

    public static TheoryData<Type, string> Actions()
    {
        var data = new TheoryData<Type, string>();
        foreach (var (controller, action) in Discover())
            data.Add(controller, action.Name);
        return data;
    }

    [Fact]
    public void EachControllerRefusesADemoSubjectUnlessAnActionIsOpened()
    {
        Controllers.Should().OnlyContain(
            c => c.GetCustomAttribute<DenyDemoSubjectAttribute>() != null,
            "an action added later is then refused to a demo subject until it is opened");
    }

    [Fact]
    public void EveryActionIsClassified()
    {
        var discovered = Discover().Select(e => Key(e.Controller, e.Action)).ToList();

        discovered.Should().BeEquivalentTo(Expected.Keys,
            "a new action on these controllers has to be placed: opened, or named here as refused");
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void EachActionOpensWhatItIsClassifiedAs(Type controller, string actionName)
    {
        var action = controller.GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance)!;

        ControllerActionReflection.OpeningOf(action).Should().Be(Expected[Key(controller, action)]);
    }

    [Fact]
    public void AReadOpeningIsOnAGetAndAStaffWriteOpeningIsNot()
    {
        foreach (var controller in ControllerActionReflection.GetControllers())
        foreach (var action in ControllerActionReflection.GetActionMethods(controller))
        {
            var methods = ControllerActionReflection.GetHttpMethods(action).ToList();
            switch (ControllerActionReflection.OpeningOf(action))
            {
                case DemoSubjectOpening.Read:
                    methods.Should().Equal(["GET"], "{0} is opened as a read", Key(controller, action));
                    break;
                case DemoSubjectOpening.StaffWrite:
                    methods.Should().NotBeEmpty().And.NotContain("GET", "{0} is opened as a write", Key(controller, action));
                    break;
            }
        }
    }

    [Fact]
    public void AnOpeningIsOnlyUnderTheControllersRefusalAndNeverBesideTheActionsOwn()
    {
        foreach (var controller in ControllerActionReflection.GetControllers())
        foreach (var action in ControllerActionReflection.GetActionMethods(controller))
        {
            if (ControllerActionReflection.OpeningOf(action) is null)
                continue;

            controller.GetCustomAttribute<DenyDemoSubjectAttribute>().Should().NotBeNull(
                "{0} opens what its controller does not refuse", Key(controller, action));
            action.GetCustomAttribute<DenyDemoSubjectAttribute>().Should().BeNull(
                "{0} both refuses and opens", Key(controller, action));
        }
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task TheClinicPersonaWritesOnlyItsOwnViewsAndRules(Type controller, string actionName)
    {
        var action = controller.GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance)!;

        var admitted = await AdmitsAsync(controller, action, inATeam: true);

        admitted.Should().Be(Expected[Key(controller, action)] is not null);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task TheVisitorInNoTeamWritesNothing(Type controller, string actionName)
    {
        var action = controller.GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance)!;

        var admitted = await AdmitsAsync(controller, action, inATeam: false);

        admitted.Should().Be(Expected[Key(controller, action)] == DemoSubjectOpening.Read);
    }

    private static IEnumerable<(Type Controller, MethodInfo Action)> Discover() =>
        Controllers.SelectMany(c => ControllerActionReflection.GetActionMethods(c).Select(a => (c, a)));

    private static string Key(Type controller, MethodInfo action) => $"{controller.Name}.{action.Name}";

    /// <summary>Runs each <see cref="DenyDemoSubjectAttribute"/> the action meets for a demo subject, as MVC would.</summary>
    private static async Task<bool> AdmitsAsync(Type controller, MethodInfo action, bool inATeam)
    {
        var subjectId = Guid.CreateVersion7();
        var subjects = new Mock<ISubjectRepository>();
        subjects.Setup(s => s.IsDemoSubjectAsync(subjectId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var teams = new Mock<ITeamRepository>();
        teams.Setup(t => t.GetTeamsOfAsync(subjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(inATeam ? [new TeamSummary(Guid.CreateVersion7(), "Clinic", TeamMemberKind.Staff)] : []);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(subjects.Object)
                .AddSingleton(teams.Object)
                .BuildServiceProvider(),
        };
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = subjectId,
        };

        var metadata = controller.GetCustomAttributes(inherit: true).Concat(action.GetCustomAttributes(inherit: true)).ToList();
        var context = new AuthorizationFilterContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor { EndpointMetadata = metadata }),
            []);

        var filters = metadata.OfType<DenyDemoSubjectAttribute>().ToList();
        filters.Should().NotBeEmpty();
        foreach (var filter in filters)
        {
            await filter.OnAuthorizationAsync(context);
            if (context.Result is not null)
                return false;
        }

        return true;
    }
}
