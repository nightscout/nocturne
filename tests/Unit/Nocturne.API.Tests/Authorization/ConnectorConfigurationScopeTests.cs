using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Controllers.V4.Connectors;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Guards the connector configuration surface against a read-only credential. The stored values
/// name the host the server fetches from and the credentials it presents, so reading them exposes
/// account identifiers and writing them redirects or silences a CGM feed. Every action there
/// requires <see cref="Scope.TenantSettings"/>; the exceptions are enumerated in
/// <see cref="Ungated"/>.
/// </summary>
/// <remarks>
/// <see cref="V4WriteScopeGatingTests"/> sweeps the whole V4 plane for a data-category scope and
/// files these controllers as an exemption, because <c>tenant.settings</c> is not one. This class
/// is what that exemption defers to: it pins the gate that is there instead, and asserts the scope
/// resolution that decides who keeps access — a seed role through <see cref="MemberScopeResolver"/>,
/// a guest grant through <see cref="Scope.ValidateGrantScopes"/>, and the CareLink desktop
/// link token.
/// </remarks>
public class ConnectorConfigurationScopeTests
{
    /// <summary>Reason marker for an action whose gate lives in the handler rather than an attribute.</summary>
    private const string HandlerGuarded = "enforced in the handler; also admits the desktop link token";

    private static readonly Type[] ConnectorSurface =
    [
        typeof(ConfigurationController),
        typeof(ConnectorStatusController),
        typeof(CareLinkConnectController),
        typeof(WebhookSettingsController),
        typeof(MyFitnessPalSettingsController),
    ];

    /// <summary>
    /// Actions on the surface that deliberately carry no <c>tenant.settings</c> attribute, with the
    /// reason. Anything else fails <see cref="EveryConnectorAction_RequiresTenantSettings"/>.
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
    public void EveryConnectorAction_RequiresTenantSettings()
    {
        var ungated = new List<string>();
        var checkedActions = 0;

        foreach (var (controller, action) in SurfaceActions())
        {
            if (Ungated.ContainsKey($"{controller.Name}.{action.Name}"))
                continue;

            checkedActions++;

            if (!RequiredScopes(controller, action).Contains(Scope.TenantSettings, StringComparer.Ordinal))
                ungated.Add($"{controller.Name}.{action.Name}");
        }

        // Sanity: the scan must find the surface, or the assertion below passes vacuously.
        // Thirteen actions carry the gate today: seven on ConfigurationController, two each on
        // WebhookSettings and MyFitnessPalSettings, and one each on ConnectorStatus and
        // CareLinkConnect.
        checkedActions.Should().BeGreaterThan(10,
            "the reflection scan should discover every action on the connector configuration surface");

        ungated.Should().BeEmpty(
            "reading or changing a connector's configuration is tenant administration, so it must "
            + "carry [RequireScope(tenant.settings)] on the action or the class, or be listed in "
            + "Ungated with a reason. Unprotected: " + string.Join("; ", ungated));
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
    [InlineData(RoleSeeds.Caretaker)]
    public void SeedRoleWithoutTenantSettings_IsDeniedTheWholeConnectorSurface(string role)
    {
        var scopes = SeedRoleScopes(role);

        scopes.Should().NotContain(Scope.TenantSettings);
        scopes.Should().NotContain(Scope.FullAccess);

        foreach (var (controller, action) in GatedActions())
        {
            Evaluate(controller, action, authenticated: true, scopes)
                .Should().BeOfType<ForbidResult>(
                    $"the {role} role holds no tenant.settings, so it must not reach "
                    + $"{controller.Name}.{action.Name}");
        }
    }

    [Fact]
    public void ReadOnlyGuestSession_IsDeniedTheWholeConnectorSurface()
    {
        foreach (var (controller, action) in GatedActions())
        {
            Evaluate(controller, action, authenticated: true, GuestScopes())
                .Should().BeOfType<ForbidResult>(
                    $"a read-only guest session must not reach {controller.Name}.{action.Name}");
        }
    }

    [Fact]
    public void UnauthenticatedCaller_IsDeniedTheWholeConnectorSurface()
    {
        var tenantSettings = new HashSet<string> { Scope.TenantSettings };

        foreach (var (controller, action) in GatedActions())
        {
            Evaluate(controller, action, authenticated: false, tenantSettings)
                .Should().BeOfType<UnauthorizedResult>(
                    $"{controller.Name}.{action.Name} must reject an unauthenticated caller");
        }
    }

    [Fact]
    public void WildcardGrant_KeepsTheWholeConnectorSurface()
    {
        // A legacy api-secret and an instance-key service credential both normalise to "*", and the
        // provisioning paths that configure a connector for a tenant authenticate that way.
        var wildcard = new HashSet<string> { Scope.FullAccess };

        foreach (var (controller, action) in GatedActions())
        {
            Evaluate(controller, action, authenticated: true, wildcard)
                .Should().BeNull($"a full-access grant must keep {controller.Name}.{action.Name}");
        }
    }

    /// <summary>
    /// A scoped credential cannot reach the connector surface however wide its membership, because
    /// <c>tenant.settings</c> is outside <see cref="Scope.ValidRequestScopes"/> and
    /// <see cref="MemberScopeResolver"/> re-normalises a credential's scopes through the request
    /// vocabulary before intersecting. Pinned so the CareLink desktop token's premise — that its
    /// bespoke scope reaches nothing else — stays true for every scoped credential.
    /// </summary>
    [Fact]
    public void ScopedCredentialOnAnAdminMembership_IsDeniedTheWholeConnectorSurface()
    {
        var scopes = MemberScopeResolver.Resolve(
            new HashSet<string>(RoleSeeds.Permissions[RoleSeeds.Admin]),
            AuthType.OAuthAccessToken,
            Scope.Normalize([Scope.HealthReadWrite]).ToHashSet());

        scopes.Should().NotContain(Scope.TenantSettings);

        foreach (var (controller, action) in GatedActions())
        {
            Evaluate(controller, action, authenticated: true, scopes)
                .Should().BeOfType<ForbidResult>(
                    $"a consented OAuth grant must not reach {controller.Name}.{action.Name}");
        }
    }

    // ── the CareLink flow, whose gate is in the handler ────────────────────────────────────────

    [Theory]
    [InlineData(RoleSeeds.Viewer)]
    [InlineData(RoleSeeds.Caretaker)]
    public async Task CareLinkFlow_RefusesASeedRoleWithoutTenantSettings(string role)
    {
        var controller = NewCareLinkController(SeedRoleScopes(role), credentialScopes: []);

        (await controller.Start(new CareLinkConnectStartRequest { Server = "EU" }, default))
            .Result.Should().BeOfType<ForbidResult>();
        (await controller.Complete(new CareLinkConnectCompleteRequest { Code = "c", State = "s" }, default))
            .Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task CareLinkFlow_RefusesAReadOnlyGuestSession()
    {
        var controller = NewCareLinkController(GuestScopes(), credentialScopes: []);

        (await controller.Start(new CareLinkConnectStartRequest { Server = "EU" }, default))
            .Result.Should().BeOfType<ForbidResult>();
        (await controller.Complete(new CareLinkConnectCompleteRequest { Code = "c", State = "s" }, default))
            .Result.Should().BeOfType<ForbidResult>();
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

    private static IEnumerable<(Type Controller, MethodInfo Action)> SurfaceActions() =>
        ConnectorSurface
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .SelectMany(c => Actions(c).Select(a => (Controller: c, Action: a)));

    private static IEnumerable<(Type Controller, MethodInfo Action)> GatedActions() =>
        SurfaceActions().Where(x => !Ungated.ContainsKey($"{x.Controller.Name}.{x.Action.Name}"));

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

    /// <summary>
    /// Runs the authorization filters an action actually declares, class-level first as MVC orders
    /// them. Returns the short-circuit result, or null when the request would reach the handler.
    /// </summary>
    private static IActionResult? Evaluate(
        Type controller, MethodInfo action, bool authenticated, IReadOnlySet<string> grantedScopes)
    {
        var actionContext = new ActionContext(
            NewHttpContext(authenticated, grantedScopes, credentialScopes: []),
            new RouteData(),
            new ActionDescriptor());

        foreach (var filter in Filters(controller, action).OfType<IAuthorizationFilter>())
        {
            var authorizationContext = new AuthorizationFilterContext(actionContext, []);
            filter.OnAuthorization(authorizationContext);
            if (authorizationContext.Result is not null)
                return authorizationContext.Result;
        }

        return null;
    }

    private static DefaultHttpContext NewHttpContext(
        bool authenticated, IReadOnlySet<string> grantedScopes, string[] credentialScopes)
    {
        var httpContext = new DefaultHttpContext();
        if (authenticated)
        {
            httpContext.Items["AuthContext"] = new AuthContext
            {
                IsAuthenticated = true,
                SubjectId = Guid.CreateVersion7(),
                Scopes = [.. credentialScopes],
            };
        }

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
                HttpContext = NewHttpContext(authenticated: true, grantedScopes, credentialScopes),
            },
        };
    }
}
