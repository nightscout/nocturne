using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Controllers.V4.Connectors;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Guards the connector configuration surface. The stored values name the host the server fetches
/// from and the credentials it presents, so reading them exposes account identifiers and writing
/// them redirects or silences a CGM feed. Configuration and status take
/// <see cref="Scope.ConnectorsRead"/> to read and <see cref="Scope.ConnectorsReadWrite"/> to change,
/// which <see cref="Scope.TenantSettings"/> satisfies; the browser connect ceremony and the separate
/// webhook and MyFitnessPal settings stay on <see cref="Scope.TenantSettings"/>. The exceptions are
/// enumerated in <see cref="Ungated"/>.
/// </summary>
/// <remarks>
/// <see cref="V4WriteScopeGatingTests"/> sweeps the whole V4 plane for a data-category scope and
/// files the <c>tenant.settings</c> controllers as an exemption. This class pins the gates and
/// asserts the scope resolution that decides who keeps access: a seed role or a scoped credential
/// through <see cref="MemberScopeResolver"/>, a guest grant through
/// <see cref="Scope.ValidateGrantScopes"/>, and the CareLink desktop link token.
/// </remarks>
public class ConnectorConfigurationScopeTests
{
    /// <summary>Reason marker for an action whose gate lives in the handler rather than an attribute.</summary>
    private const string HandlerGuarded = "enforced in the handler; also admits the desktop link token";

    private static readonly Type[] ConnectorScopedSurface =
    [
        typeof(ConfigurationController),
        typeof(ConnectorStatusController),
    ];

    private static readonly Type[] TenantSettingsSurface =
    [
        typeof(CareLinkConnectController),
        typeof(WebhookSettingsController),
        typeof(MyFitnessPalSettingsController),
    ];

    private static readonly AuthType[] ScopedCredentialTypes =
        [AuthType.OAuthAccessToken, AuthType.DirectGrant, AuthType.ApiKey];

    /// <summary>
    /// Actions on the surface that deliberately carry no scope attribute, with the reason. Anything
    /// else fails <see cref="EveryConnectorAction_RequiresItsScope"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Ungated =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The connector's declared field list, not anything a tenant configured, and
            // deliberately [AllowAnonymous] so the setup wizard can render the form before there is
            // a tenant to be a member of.
            ["ConfigurationController.GetSchema"] = "connector metadata, no tenant state",

            // Returns a fixed disabled stub regardless of what the tenant configured — the webhook
            // channel is not wired to the current alert engine's storage.
            ["WebhookSettingsController.GetWebhookSettings"] = "fixed stub, no tenant state",

            ["CareLinkConnectController.Start"] = HandlerGuarded,
            ["CareLinkConnectController.Complete"] = HandlerGuarded,
        };

    [Fact]
    public void EveryConnectorAction_RequiresItsScope()
    {
        var wrong = new List<string>();
        var checkedActions = 0;

        foreach (var (controller, action) in GatedActions())
        {
            checkedActions++;

            var expected = ExpectedScope(controller, action);
            var required = RequiredScopes(controller, action).ToList();
            if (required is not [var only] || only != expected)
                wrong.Add($"{controller.Name}.{action.Name} requires [{string.Join(",", required)}], expected {expected}");
        }

        // Sanity: the scan must find the surface, or the assertion below passes vacuously.
        checkedActions.Should().BeGreaterThan(10,
            "the reflection scan should discover every action on the connector configuration surface");

        wrong.Should().BeEmpty(
            "configuration and status reads take connectors.read and their writes connectors.readwrite; "
            + "the other connector settings take tenant.settings; anything ungated is listed in "
            + "Ungated with a reason. Wrong: " + string.Join("; ", wrong));
    }

    [Fact]
    public void EveryUngatedEntry_NamesALiveAction()
    {
        // An entry left behind after its action was gated, renamed or deleted would silently excuse
        // a future action that reuses the name.
        var actions = SurfaceActions()
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .ToHashSet(StringComparer.Ordinal);

        Ungated.Keys.Should().BeSubsetOf(actions);
    }

    [Theory]
    [InlineData(RoleSeeds.Owner)]
    [InlineData(RoleSeeds.Admin)]
    public void SeedRoleHoldingTenantSettings_KeepsTheWholeConnectorSurface(string role)
    {
        var scopes = SeedRoleScopes(role);

        foreach (var (controller, action) in GatedActions())
        {
            Evaluate(controller, action, authenticated: true, scopes)
                .Should().BeNull(
                    $"the {role} role administers the tenant, so it must keep {controller.Name}.{action.Name}");
        }
    }

    [Theory]
    [InlineData(RoleSeeds.Viewer)]
    [InlineData(RoleSeeds.Clinician)]
    public void SeedRoleWithoutTenantSettings_IsDeniedTheWholeConnectorSurface(string role)
    {
        var scopes = SeedRoleScopes(role);

        scopes.Should().NotContain(Scope.TenantSettings);
        scopes.Should().NotContain(Scope.FullAccess);

        foreach (var (controller, action) in GatedActions())
        {
            Evaluate(controller, action, authenticated: true, scopes)
                .Should().BeOfType<ScopeForbiddenResult>(
                    $"the {role} role holds no tenant.settings, so it must not reach "
                    + $"{controller.Name}.{action.Name}");
        }
    }

    [Fact]
    public void Caretaker_SetsUpDataSourcesAndReachesNoOtherConnectorSetting()
    {
        var scopes = SeedRoleScopes(RoleSeeds.Caretaker);

        scopes.Should().NotContain(Scope.TenantSettings);
        scopes.Should().NotContain(Scope.FullAccess);

        foreach (var (controller, action) in ConnectorScopedActions())
        {
            Evaluate(controller, action, authenticated: true, scopes)
                .Should().BeNull($"a caretaker sets up data sources, so it must reach {controller.Name}.{action.Name}");
        }

        foreach (var (controller, action) in TenantSettingsActions().Append(TenantSettingsRead()))
        {
            Evaluate(controller, action, authenticated: true, scopes)
                .Should().BeOfType<ScopeForbiddenResult>(
                    $"a caretaker holds no tenant.settings, so it must not reach {controller.Name}.{action.Name}");
        }
    }

    [Fact]
    public void ReadOnlyGuestSession_IsDeniedTheWholeConnectorSurface()
    {
        foreach (var (controller, action) in GatedActions())
        {
            Evaluate(controller, action, authenticated: true, GuestScopes())
                .Should().BeOfType<ScopeForbiddenResult>(
                    $"a read-only guest session must not reach {controller.Name}.{action.Name}");
        }
    }

    [Fact]
    public void UnauthenticatedCaller_IsDeniedEveryWrite()
    {
        var tenantSettings = new HashSet<string> { Scope.TenantSettings };

        foreach (var (controller, action) in GatedActions().Where(x => ExpectedScope(x.Controller, x.Action) != Scope.ConnectorsRead))
        {
            Evaluate(controller, action, authenticated: false, tenantSettings)
                .Should().BeOfType<UnauthorizedResult>(
                    $"{controller.Name}.{action.Name} must reject an unauthenticated caller");
        }
    }

    /// <summary>
    /// <see cref="Scope.ConnectorsRead"/> is a read, so <see cref="RequireScopeAttribute"/> decides it
    /// on the scope set alone for an anonymous caller. The only anonymous principal with scopes is
    /// a public share, capped at <see cref="Scope.PublicShareScopes"/>.
    /// </summary>
    [Fact]
    public void PublicShare_IsDeniedTheWholeConnectorSurface()
    {
        foreach (var (controller, action) in GatedActions())
        {
            Evaluate(controller, action, authenticated: false, Scope.PublicShareScopes)
                .Should().NotBeNull($"a public share must not reach {controller.Name}.{action.Name}");
        }
    }

    [Theory]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.InstanceKey)]
    [InlineData(AuthType.PlatformAccess)]
    public void WildcardGrant_KeepsTheWholeConnectorSurface(AuthType authType)
    {
        // A legacy api-secret and an instance-key service credential both normalise to "*", and the
        // provisioning paths that configure a connector for a tenant authenticate that way.
        var wildcard = new HashSet<string> { Scope.FullAccess };

        foreach (var (controller, action) in GatedActions())
        {
            Evaluate(controller, action, authenticated: true, wildcard, authType)
                .Should().BeNull($"a full-access grant must keep {controller.Name}.{action.Name}");
        }
    }

    [Theory]
    [MemberData(nameof(AdministeringMembershipsOnScopedCredentials))]
    public void HealthReadWriteCredential_IsDeniedTheWholeConnectorSurface(string role, AuthType authType)
    {
        var scopes = ScopedCredentialScopes(role, authType, Scope.HealthReadWrite);

        foreach (var (controller, action) in GatedActions())
        {
            Evaluate(controller, action, authenticated: true, scopes, authType)
                .Should().BeOfType<ScopeForbiddenResult>(
                    "connector management is not health data, so health.readwrite must not reach "
                    + $"{controller.Name}.{action.Name}");
        }
    }

    [Theory]
    [MemberData(nameof(ConnectorSetupMembershipsOnScopedCredentials))]
    public void ConnectorsReadWriteCredential_ReachesTheConnectorScopedSurfaceAndNothingElse(
        string role, AuthType authType)
    {
        var scopes = ScopedCredentialScopes(role, authType, Scope.ConnectorsReadWrite);

        scopes.Should().NotContain(Scope.TenantSettings, "a credential is never widened by its membership");

        foreach (var (controller, action) in ConnectorScopedActions())
        {
            Evaluate(controller, action, authenticated: true, scopes, authType)
                .Should().BeNull($"a connectors.readwrite credential on a {role} membership must reach "
                    + $"{controller.Name}.{action.Name}");
        }

        foreach (var (controller, action) in TenantSettingsActions().Append(TenantSettingsRead()))
        {
            Evaluate(controller, action, authenticated: true, scopes, authType)
                .Should().BeOfType<ScopeForbiddenResult>(
                    $"connectors.readwrite is not tenant.settings, so it must not reach {controller.Name}.{action.Name}");
        }
    }

    [Theory]
    [MemberData(nameof(AdministeringMembershipsOnScopedCredentials))]
    public void ConnectorsReadCredential_ReachesTheReadsAndIsDeniedEveryWrite(string role, AuthType authType)
    {
        var scopes = ScopedCredentialScopes(role, authType, Scope.ConnectorsRead);
        var reads = 0;
        var writes = 0;

        foreach (var (controller, action) in ConnectorScopedActions())
        {
            var result = Evaluate(controller, action, authenticated: true, scopes, authType);

            if (IsRead(action))
            {
                reads++;
                result.Should().BeNull($"connectors.read must reach {controller.Name}.{action.Name}");
            }
            else
            {
                writes++;
                result.Should().BeOfType<ScopeForbiddenResult>(
                    $"connectors.read must not reach {controller.Name}.{action.Name}");
            }
        }

        reads.Should().BeGreaterThan(0);
        writes.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(RoleSeeds.Viewer)]
    [InlineData(RoleSeeds.Clinician)]
    [InlineData(RoleSeeds.Denied)]
    public void ConnectorsReadWriteCredential_OnAMembershipWithoutConnectorSetup_IsDenied(string role)
    {
        var scopes = ScopedCredentialScopes(role, AuthType.OAuthAccessToken, Scope.ConnectorsReadWrite);

        foreach (var (controller, action) in GatedActions())
        {
            Evaluate(controller, action, authenticated: true, scopes, AuthType.OAuthAccessToken)
                .Should().BeOfType<ScopeForbiddenResult>(
                    $"the {role} role holds neither tenant.settings nor a connector scope, so its credential "
                    + $"must not reach {controller.Name}.{action.Name}");
        }
    }

    public static TheoryData<string, AuthType> AdministeringMembershipsOnScopedCredentials() =>
        MembershipsOnScopedCredentials(RoleSeeds.Owner, RoleSeeds.Admin);

    public static TheoryData<string, AuthType> ConnectorSetupMembershipsOnScopedCredentials() =>
        MembershipsOnScopedCredentials(RoleSeeds.Owner, RoleSeeds.Admin, RoleSeeds.Caretaker);

    private static TheoryData<string, AuthType> MembershipsOnScopedCredentials(params string[] roles)
    {
        var data = new TheoryData<string, AuthType>();
        foreach (var role in roles)
            foreach (var authType in ScopedCredentialTypes)
                data.Add(role, authType);
        return data;
    }

    // ── the CareLink flow, whose gate is in the handler ────────────────────────────────────────

    [Theory]
    [InlineData(RoleSeeds.Viewer)]
    [InlineData(RoleSeeds.Caretaker)]
    public async Task CareLinkFlow_RefusesASeedRoleWithoutTenantSettings(string role)
    {
        var controller = NewCareLinkController(SeedRoleScopes(role), credentialScopes: []);

        (await controller.Start(new CareLinkConnectStartRequest { Server = "EU" }, default))
            .Result.Should().BeOfType<ScopeForbiddenResult>();
        (await controller.Complete(new CareLinkConnectCompleteRequest { Code = "c", State = "s" }, default))
            .Result.Should().BeOfType<ScopeForbiddenResult>();
    }

    [Fact]
    public async Task CareLinkFlow_RefusesAReadOnlyGuestSession()
    {
        var controller = NewCareLinkController(GuestScopes(), credentialScopes: []);

        (await controller.Start(new CareLinkConnectStartRequest { Server = "EU" }, default))
            .Result.Should().BeOfType<ScopeForbiddenResult>();
        (await controller.Complete(new CareLinkConnectCompleteRequest { Code = "c", State = "s" }, default))
            .Result.Should().BeOfType<ScopeForbiddenResult>();
    }

    /// <summary>
    /// An administrator and a desktop link token both get past the guard. Asserted by driving each
    /// action into its own input validation — an unknown region, and a state with no cached flow —
    /// which is the first thing after the guard and needs no network.
    /// </summary>
    [Fact]
    public async Task CareLinkFlow_AdmitsAnAdministratorAndADesktopLinkToken()
    {
        var admin = NewCareLinkController(
            SeedRoleScopes(RoleSeeds.Admin), credentialScopes: []);

        // The desktop token's scope survives no intersection, so its resolved scope set is empty.
        var desktop = NewCareLinkController(
            new HashSet<string>(), credentialScopes: ["connectors:carelink:connect"]);

        foreach (var controller in new[] { admin, desktop })
        {
            (await controller.Start(new CareLinkConnectStartRequest { Server = "XX" }, default))
                .Result.Should().BeOfType<BadRequestObjectResult>();
            (await controller.Complete(new CareLinkConnectCompleteRequest { Code = "c", State = "s" }, default))
                .Result.Should().BeOfType<BadRequestObjectResult>();
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static IReadOnlySet<string> SeedRoleScopes(string role) =>
        MemberScopeResolver.Resolve(
            new HashSet<string>(RoleSeeds.Permissions[role]),
            AuthType.SessionCookie,
            new HashSet<string>());

    /// <summary>The widest grant a guest link can hold: <see cref="Scope.AllowedGuestScopes"/>, read-only.</summary>
    private static IReadOnlySet<string> GuestScopes() =>
        Scope.Normalize(
            Scope.ValidateGrantScopes(Scope.AllowedGuestScopes, OAuthGrantTypes.Guest));

    private static IReadOnlySet<string> ScopedCredentialScopes(string role, AuthType authType, string credentialScope) =>
        MemberScopeResolver.Resolve(
            new HashSet<string>(RoleSeeds.Permissions[role]),
            authType,
            new HashSet<string> { credentialScope });

    private static IEnumerable<(Type Controller, MethodInfo Action)> ActionsOf(IEnumerable<Type> controllers) =>
        controllers
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .SelectMany(c => Actions(c).Select(a => (Controller: c, Action: a)));

    private static IEnumerable<(Type Controller, MethodInfo Action)> SurfaceActions() =>
        ActionsOf([.. ConnectorScopedSurface, .. TenantSettingsSurface]);

    private static IEnumerable<(Type Controller, MethodInfo Action)> GatedActions() =>
        SurfaceActions().Where(x => !Ungated.ContainsKey($"{x.Controller.Name}.{x.Action.Name}"));

    private static IEnumerable<(Type Controller, MethodInfo Action)> ConnectorScopedActions() =>
        GatedActions().Where(x => ConnectorScopedSurface.Contains(x.Controller));

    private static IEnumerable<(Type Controller, MethodInfo Action)> TenantSettingsActions() =>
        GatedActions().Where(x => TenantSettingsSurface.Contains(x.Controller));

    private static (Type Controller, MethodInfo Action) TenantSettingsRead() =>
        (typeof(TenantSettingsController),
            typeof(TenantSettingsController).GetMethod(nameof(TenantSettingsController.GetTenantSettings))!);

    private static bool IsRead(MethodInfo action) =>
        action.GetCustomAttributes(inherit: true).OfType<IActionHttpMethodProvider>()
            .SelectMany(p => p.HttpMethods ?? [])
            .All(verb => verb == "GET");

    private static string ExpectedScope(Type controller, MethodInfo action) =>
        !ConnectorScopedSurface.Contains(controller) ? Scope.TenantSettings
        : IsRead(action) ? Scope.ConnectorsRead
        : Scope.ConnectorsReadWrite;

    /// <summary>The methods MVC would route: declared on the controller, not a property accessor.</summary>
    private static IEnumerable<MethodInfo> Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.GetCustomAttributes(inherit: true).OfType<IActionHttpMethodProvider>().Any())
            .OrderBy(m => m.Name, StringComparer.Ordinal);

    /// <summary>The scopes an action's gate requires, from the class and the action together.</summary>
    private static IEnumerable<string> RequiredScopes(Type controller, MethodInfo action) =>
        Filters(controller, action).OfType<RequireScopeAttribute>().SelectMany(a => a.Scopes);

    private static object[] Filters(Type controller, MethodInfo action) =>
        [.. controller.GetCustomAttributes(inherit: true), .. action.GetCustomAttributes(inherit: true)];

    private static IActionResult? Evaluate(
        Type controller, MethodInfo action, bool authenticated, IReadOnlySet<string> grantedScopes,
        AuthType authType = AuthType.SessionCookie) =>
        ActionGate.Evaluate(
            controller, action, grantedScopes, authenticated ? NewAuthContext(authType, credentialScopes: []) : null);

    private static AuthContext NewAuthContext(AuthType authType, string[] credentialScopes) => new()
    {
        IsAuthenticated = true,
        AuthType = authType,
        SubjectId = Guid.CreateVersion7(),
        Scopes = [.. credentialScopes],
    };

    private static DefaultHttpContext NewHttpContext(IReadOnlySet<string> grantedScopes, string[] credentialScopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = NewAuthContext(AuthType.SessionCookie, credentialScopes);
        httpContext.Items["GrantedScopes"] = grantedScopes;
        return httpContext;
    }

    private static CareLinkConnectController NewCareLinkController(
        IReadOnlySet<string> grantedScopes, string[] credentialScopes)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(a => a.Context)
            .Returns(new TenantContext(Guid.CreateVersion7(), "acme", "Acme", IsActive: true, IsDemo: false));

        return new CareLinkConnectController(
            Mock.Of<IConnectorConfigurationService>(),
            new MemoryCache(new MemoryCacheOptions()),
            tenantAccessor.Object,
            Mock.Of<IJwtService>(),
            NullLoggerFactory.Instance,
            NullLogger<CareLinkConnectController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = NewHttpContext(grantedScopes, credentialScopes),
            },
        };
    }
}
