using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Guards the administering actions on <see cref="ServicesController"/>. Deleting a data source's,
/// a connection's or a connector's data, and deleting demo data, remove health records and carry
/// <see cref="Scope.TenantSettings"/>. Triggering a connector sync and resetting a connector's cursor
/// only drive a connector and carry <see cref="Scope.ConnectorsReadWrite"/>; the connector state
/// reads carry <see cref="Scope.ConnectorsRead"/>. Every administering action also carries
/// <see cref="DenyDemoSubjectAttribute"/>.
/// </summary>
/// <remarks>
/// <see cref="V4WriteScopeGatingTests"/> maps each write action to its scope; this class is the
/// behavioural half that pins who keeps access. The gates must admit the Administrator seed role,
/// which holds <see cref="Scope.TenantSettings"/> but not <see cref="Scope.FullAccess"/>, so a
/// superuser gate here would lock it out.
/// </remarks>
public class ServicesControllerScopeTests
{
    /// <summary>
    /// The administering actions and the scope each requires, pinned by name so a rename cannot drop
    /// the gate silently.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AdministeringActionScopes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(ServicesController.DeleteDemoData)] = Scope.TenantSettings,
            [nameof(ServicesController.DeleteDataSourceData)] = Scope.TenantSettings,
            [nameof(ServicesController.DeleteConnectionRecords)] = Scope.TenantSettings,
            [nameof(ServicesController.DeleteConnectorData)] = Scope.TenantSettings,
            [nameof(ServicesController.TriggerConnectorSync)] = Scope.ConnectorsReadWrite,
            [nameof(ServicesController.ResetConnectorCursor)] = Scope.ConnectorsReadWrite,
        };

    private static readonly string[] ConnectorReadActions =
    [
        nameof(ServicesController.GetAvailableConnectors),
        nameof(ServicesController.GetConnectorDataSummary),
        nameof(ServicesController.GetConnectorSyncStatus),
    ];

    private static IEnumerable<string> AdministeringActions => AdministeringActionScopes.Keys;

    [Theory]
    [MemberData(nameof(AdministeringActionNames))]
    public void AdministeringAction_RequiresItsScopeAndRefusesTheDemoSubject(string actionName)
    {
        var action = Action(actionName);

        action.GetCustomAttributes(inherit: true).OfType<RequireScopeAttribute>()
            .SelectMany(a => a.Scopes)
            .Should().Equal(AdministeringActionScopes[actionName]);

        action.GetCustomAttributes(inherit: true).OfType<DenyDemoSubjectAttribute>()
            .Should().NotBeEmpty("the demo member holds tenant.settings, so the shared demo account "
                + "must be refused by attribute rather than by permission");
    }

    [Theory]
    [MemberData(nameof(ConnectorReadActionNames))]
    public void ConnectorReadAction_RequiresConnectorsRead(string actionName)
    {
        Action(actionName).GetCustomAttributes(inherit: true).OfType<RequireScopeAttribute>()
            .SelectMany(a => a.Scopes)
            .Should().Equal(Scope.ConnectorsRead);
    }

    [Fact]
    public void ConnectorsReadWriteCredentialOnAnOwnerMembership_DrivesConnectorsButCannotDeleteData()
    {
        var scopes = MemberScopeResolver.Resolve(
            new HashSet<string>(RoleSeeds.Permissions[RoleSeeds.Owner]),
            AuthType.OAuthAccessToken,
            new HashSet<string> { Scope.ConnectorsReadWrite });

        foreach (var actionName in ConnectorReadActions.Concat(
                     AdministeringActions.Where(a => AdministeringActionScopes[a] == Scope.ConnectorsReadWrite)))
        {
            Evaluate(actionName, authenticated: true, scopes)
                .Should().BeNull($"connectors.readwrite must reach {actionName}");
        }

        foreach (var actionName in AdministeringActions.Where(a => AdministeringActionScopes[a] == Scope.TenantSettings))
        {
            Evaluate(actionName, authenticated: true, scopes)
                .Should().BeOfType<ScopeForbiddenResult>(
                    $"deleting health records is tenant administration, so connectors.readwrite must not reach {actionName}");
        }
    }

    [Theory]
    [MemberData(nameof(AdministeringActionNames))]
    [MemberData(nameof(ConnectorReadActionNames))]
    public void Administrator_PassesTheScopeFilter(string actionName)
    {
        Evaluate(actionName, authenticated: true, SeedRoleScopes(RoleSeeds.Admin))
            .Should().BeNull(
                "the Administrator role holds tenant.settings, so it must administer services");
    }

    [Fact]
    public void Caretaker_DrivesConnectorsButCannotDeleteData()
    {
        var scopes = SeedRoleScopes(RoleSeeds.Caretaker);

        foreach (var actionName in ConnectorReadActions.Concat(
                     AdministeringActions.Where(a => AdministeringActionScopes[a] == Scope.ConnectorsReadWrite)))
        {
            Evaluate(actionName, authenticated: true, scopes)
                .Should().BeNull($"a caretaker sets up data sources, so it must reach {actionName}");
        }

        foreach (var actionName in AdministeringActions.Where(a => AdministeringActionScopes[a] == Scope.TenantSettings))
        {
            Evaluate(actionName, authenticated: true, scopes)
                .Should().BeOfType<ScopeForbiddenResult>(
                    $"deleting health records is tenant administration, so a caretaker must not reach {actionName}");
        }
    }

    [Theory]
    [InlineData(RoleSeeds.Viewer)]
    [InlineData(RoleSeeds.Clinician)]
    public void SeedRoleWithoutTenantSettings_IsRefused(string role)
    {
        var scopes = SeedRoleScopes(role);

        scopes.Should().NotContain(Scope.TenantSettings);
        scopes.Should().NotContain(Scope.FullAccess);

        foreach (var actionName in AdministeringActions.Concat(ConnectorReadActions))
        {
            Evaluate(actionName, authenticated: true, scopes)
                .Should().BeOfType<ScopeForbiddenResult>(
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

    public static TheoryData<string> AdministeringActionNames() => [.. AdministeringActions];

    public static TheoryData<string> ConnectorReadActionNames() => [.. ConnectorReadActions];

    private static MethodInfo Action(string name) =>
        typeof(ServicesController).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException(
            $"{nameof(ServicesController)} has no action named {name}; the gate moved with it");

    private static IReadOnlySet<string> SeedRoleScopes(string role) =>
        MemberScopeResolver.Resolve(
            new HashSet<string>(RoleSeeds.Permissions[role]),
            AuthType.SessionCookie,
            new HashSet<string>());

    /// <remarks>
    /// The demo-subject filter is an <see cref="IAsyncAuthorizationFilter"/> and reads the database,
    /// so <see cref="ActionGate"/> does not run it; it is asserted by its own tests, and presence is
    /// pinned above.
    /// </remarks>
    private static IActionResult? Evaluate(
        string actionName, bool authenticated, IReadOnlySet<string> grantedScopes) =>
        ActionGate.Evaluate(
            typeof(ServicesController),
            Action(actionName),
            grantedScopes,
            authenticated ? new AuthContext { IsAuthenticated = true, SubjectId = Guid.CreateVersion7() } : null);
}
