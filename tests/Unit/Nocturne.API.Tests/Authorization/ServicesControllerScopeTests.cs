using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Guards the tenant-administration actions on <see cref="ServicesController"/>: deleting a data
/// source's or a connector's data, deleting demo data, triggering a connector sync, and resetting a
/// connector's cursor. Each is the same tenant-administration decision as connector configuration,
/// so each carries <see cref="DenyDemoSubjectAttribute"/> and <see cref="Scope.TenantSettings"/>.
/// </summary>
/// <remarks>
/// <see cref="V4WriteScopeGatingTests"/> files this controller as a <c>tenant.settings</c>
/// exemption; this class is the behavioural half that pins who keeps access. The gate must admit the
/// Administrator seed role, which holds <see cref="Scope.TenantSettings"/> but not
/// <see cref="Scope.FullAccess"/>, so a superuser gate here would lock it out.
/// </remarks>
public class ServicesControllerScopeTests
{
    /// <summary>The tenant-administration actions, pinned by name so a rename cannot drop the gate silently.</summary>
    private static readonly string[] AdministeringActions =
    [
        nameof(ServicesController.DeleteDemoData),
        nameof(ServicesController.DeleteDataSourceData),
        nameof(ServicesController.DeleteConnectorData),
        nameof(ServicesController.TriggerConnectorSync),
        nameof(ServicesController.ResetConnectorCursor),
    ];

    [Theory]
    [MemberData(nameof(AdministeringActionNames))]
    public void AdministeringAction_RequiresTenantSettingsAndRefusesTheDemoSubject(string actionName)
    {
        var action = Action(actionName);

        action.GetCustomAttributes(inherit: true).OfType<RequireScopeAttribute>()
            .SelectMany(a => a.Scopes)
            .Should().Contain(Scope.TenantSettings);

        action.GetCustomAttributes(inherit: true).OfType<DenyDemoSubjectAttribute>()
            .Should().NotBeEmpty("the demo member holds tenant.settings, so the shared demo account "
                + "must be refused by attribute rather than by permission");
    }

    [Theory]
    [MemberData(nameof(AdministeringActionNames))]
    public void Administrator_PassesTheScopeFilter(string actionName)
    {
        Evaluate(actionName, authenticated: true, SeedRoleScopes(RoleSeeds.Admin))
            .Should().BeNull(
                "the Administrator role holds tenant.settings, so it must administer services");
    }

    [Theory]
    [InlineData(RoleSeeds.Viewer)]
    [InlineData(RoleSeeds.Caretaker)]
    public void SeedRoleWithoutTenantSettings_IsRefused(string role)
    {
        var scopes = SeedRoleScopes(role);

        scopes.Should().NotContain(Scope.TenantSettings);
        scopes.Should().NotContain(Scope.FullAccess);

        foreach (var actionName in AdministeringActions)
        {
            Evaluate(actionName, authenticated: true, scopes)
                .Should().BeOfType<ForbidResult>(
                    $"the {role} role holds no tenant.settings, so it must not reach {actionName}");
        }
    }

    [Fact]
    public void UnauthenticatedCaller_IsRefused()
    {
        foreach (var actionName in AdministeringActions)
        {
            Evaluate(actionName, authenticated: false, new HashSet<string> { Scope.TenantSettings })
                .Should().BeOfType<UnauthorizedResult>();
        }
    }

    public static TheoryData<string> AdministeringActionNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in AdministeringActions)
            data.Add(name);
        return data;
    }

    private static MethodInfo Action(string name) =>
        typeof(ServicesController).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException(
            $"{nameof(ServicesController)} has no action named {name}; the gate moved with it");

    private static IReadOnlySet<string> SeedRoleScopes(string role) =>
        MemberScopeResolver.Resolve(
            new HashSet<string>(RoleSeeds.Permissions[role]),
            AuthType.SessionCookie,
            new HashSet<string>());

    /// <summary>
    /// Runs the authorization filters the action declares, so the assertion covers the resolved
    /// scope decision rather than the attribute's mere presence. The demo-subject filter is an
    /// <see cref="IAsyncAuthorizationFilter"/> and reads the database; it is asserted by its own
    /// tests, and presence is pinned above.
    /// </summary>
    private static IActionResult? Evaluate(
        string actionName, bool authenticated, IReadOnlySet<string> grantedScopes)
    {
        var actionContext = new ActionContext(
            NewHttpContext(authenticated, grantedScopes), new RouteData(), new ActionDescriptor());

        foreach (var filter in Action(actionName).GetCustomAttributes(inherit: true)
                     .OfType<IAuthorizationFilter>())
        {
            var authorizationContext = new AuthorizationFilterContext(actionContext, []);
            filter.OnAuthorization(authorizationContext);
            if (authorizationContext.Result is not null)
                return authorizationContext.Result;
        }

        return null;
    }

    private static DefaultHttpContext NewHttpContext(
        bool authenticated, IReadOnlySet<string> grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        if (authenticated)
        {
            httpContext.Items["AuthContext"] = new AuthContext
            {
                IsAuthenticated = true,
                SubjectId = Guid.CreateVersion7(),
            };
        }

        httpContext.Items["GrantedScopes"] = grantedScopes;
        return httpContext;
    }
}
