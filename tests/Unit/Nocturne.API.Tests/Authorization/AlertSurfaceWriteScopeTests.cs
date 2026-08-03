using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Guards the alert surface — the controllers under
/// <c>Nocturne.API.Controllers.V4.Monitoring</c> — against a write reachable with read-only
/// credentials. Every action there that changes alert state requires
/// <see cref="OAuthScopes.AlertsReadWrite"/>, because a rule decides whether a low-glucose alert
/// reaches anyone, a DND window suppresses delivery, and an acknowledgement closes a firing
/// excursion. The exceptions are enumerated: <see cref="Ungated"/> for actions that change no alert
/// state, and <see cref="DeviceCapabilityActions"/> for the acknowledgement paths that additionally
/// accept a device capability scope.
/// </summary>
/// <remarks>
/// The namespace sweep in <see cref="V4WriteScopeGatingTests"/> covers the whole V4 plane and its
/// exemption map; this class pins the alert surface specifically, so a future Monitoring controller
/// cannot be waved through by adding it to that map, and asserts the scope resolution that decides
/// who keeps access — a seed role through <see cref="MemberScopeResolver"/>, a guest grant through
/// <see cref="OAuthScopes.ValidateGrantScopes"/>, and a client device's capability grant.
/// </remarks>
public class AlertSurfaceWriteScopeTests
{
    private const string MonitoringNamespace = "Nocturne.API.Controllers.V4.Monitoring";

    private static readonly string[] WriteVerbs = ["POST", "PUT", "PATCH", "DELETE"];

    /// <summary>
    /// The scopes the desktop Companion's OAuth grant holds (<c>DEFAULT_SCOPES</c> in
    /// <c>src/Web/packages/desktop/src-tauri/src/auth.rs</c>). No alert scope: the companion is a
    /// read-scoped poller that additionally registers as an actuation target.
    /// </summary>
    private static readonly string[] CompanionGrantScopes =
    [
        OAuthScopes.GlucoseRead, OAuthScopes.TherapyRead, OAuthScopes.DevicesRead,
        OAuthScopes.DeviceNotify, OAuthScopes.DeviceActuate,
    ];

    /// <summary>
    /// Write actions on the alert surface that deliberately carry no scope gate, with the reason.
    /// Anything else fails <see cref="EveryAlertSurfaceWriteAction_RequiresAlertsReadWrite"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Ungated =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Both actions read the saved rules over a historical window and return the events that
            // would have fired. AlertReplayService has no SaveChangesAsync and adds to no DbSet, so
            // these are reads the request-body shape forced off GET; gating them would deny replay
            // to an alerts.read holder.
            ["AlertReplayController.Replay"] = "reads and returns; persists nothing",
            ["AlertReplayController.ReplayDryRun"] = "reads and returns; persists nothing",

            // The caller's own notification bookkeeping — read_at, and the archive flags — on a row
            // InAppNotificationService confines to the caller's subject. Neither acknowledges nor
            // silences an excursion: archiving an alert.firing notification leaves escalation
            // running. Gating them on alerts.readwrite pinned a bell badge that a Clinician or
            // Viewer could never clear. V4WriteScopeGatingTests records the same decision under its
            // presentation-state category, alongside CoachMarkController and
            // UserPreferencesController.
            ["NotificationsController.MarkAsRead"] = "read state on the caller's own row",
            ["NotificationsController.MarkAllAsRead"] = "read state on the caller's own rows",
            ["NotificationsController.DismissNotification"] = "archive flag on the caller's own row",
        };

    /// <summary>
    /// The two alert-surface writes that also accept a client device's capability scope, and why.
    /// <c>AlertsController.AcknowledgeExcursion</c> calls
    /// <c>IAlertAcknowledgementService.AcknowledgeExcursionAsync</c> outright, from a device toast's
    /// Acknowledge button. <c>NotificationsController.ExecuteAction</c> is a generic dispatcher:
    /// <c>InAppNotificationService.ExecuteActionAsync</c> hands the action to whichever
    /// <c>INotificationActionHandler</c> is registered for the notification's type, of which
    /// <c>AlertActionHandler</c> — the one that acknowledges the excursion — is only one. The
    /// action confines the <c>device.notify</c> arm to <c>InAppProvider.NotificationType</c> before
    /// dispatching, so a caller admitted only by that scope reaches no other handler; that check is
    /// pinned by <c>NotificationsControllerExecuteActionScopeTests</c>. Membership here is asserted
    /// exhaustively by <see cref="OnlyTheExcursionAckPaths_AcceptADeviceCapabilityScope"/> so the
    /// exception cannot spread to a third action unnoticed.
    /// </summary>
    private static readonly string[] DeviceCapabilityActions =
    [
        "AlertsController.AcknowledgeExcursion",
        "NotificationsController.ExecuteAction",
    ];

    [Fact]
    public void EveryAlertSurfaceWriteAction_RequiresAlertsReadWrite()
    {
        var ungated = new List<string>();
        var wrongScope = new List<string>();
        var checkedActions = 0;

        foreach (var controller in MonitoringControllers())
        {
            foreach (var action in WriteActions(controller))
            {
                var key = $"{controller.Name}.{action.Name}";
                if (Ungated.ContainsKey(key))
                    continue;

                checkedActions++;

                var required = RequiredScopes(controller, action).ToList();
                if (required.Count == 0)
                {
                    ungated.Add($"{key} [{string.Join(",", HttpVerbs(action))}]");
                    continue;
                }

                if (!required.Contains(OAuthScopes.AlertsReadWrite, StringComparer.Ordinal))
                    wrongScope.Add($"{key} requires {string.Join("/", required)}");
            }
        }

        // Sanity: the sweep must find the surface, or the assertions below pass vacuously. The
        // floor tracks the namespace sweep (31 gated write actions today: 21 carrying their own
        // [RequireScope] plus TrackersController's 10 declared ones), so a sweep that silently
        // narrows to a subset fails here rather than passing on the remainder.
        checkedActions.Should().BeGreaterThan(30,
            "the reflection scan should discover every write action under " + MonitoringNamespace);

        ungated.Should().BeEmpty(
            "a write on the alert surface must carry [RequireScope(alerts.readwrite)] or "
            + "[RequireDeclaredWriteScope], or be listed in Ungated with a reason. Unprotected: "
            + string.Join("; ", ungated));

        wrongScope.Should().BeEmpty(
            "every alert-surface write must name alerts.readwrite: " + string.Join("; ", wrongScope));
    }

    [Fact]
    public void EveryUngatedEntry_NamesALiveWriteAction()
    {
        // An entry left behind after its action was gated, renamed or deleted would silently excuse
        // a future action that reuses the name.
        var writeActions = MonitoringControllers()
            .SelectMany(c => WriteActions(c).Select(a => $"{c.Name}.{a.Name}"))
            .ToHashSet(StringComparer.Ordinal);

        Ungated.Keys.Should().BeSubsetOf(writeActions);
        writeActions.Should().Contain(DeviceCapabilityActions);
    }

    [Fact]
    public void OwnerSession_KeepsEveryAlertSurfaceWrite()
    {
        // A browser session is AuthType.SessionCookie, which carries no scopes of its own, so
        // membership is the whole authority and the owner's "*" is published raw.
        var ownerScopes = MemberScopeResolver.Resolve(
            new HashSet<string>(TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Owner]),
            AuthType.SessionCookie,
            new HashSet<string>());

        ownerScopes.Should().Contain(TenantPermissions.Superuser);

        foreach (var (controller, action) in GatedWriteActions())
        {
            Evaluate(controller, action, authenticated: true, ownerScopes)
                .Should().BeNull($"a tenant owner's session must keep {controller.Name}.{action.Name}");
        }
    }

    [Theory]
    [InlineData(TenantPermissions.SeedRoles.Admin)]
    [InlineData(TenantPermissions.SeedRoles.Caretaker)]
    public void SeedRoleHoldingAlertsReadWrite_KeepsEveryAlertSurfaceWrite(string role)
    {
        var scopes = MemberScopeResolver.Resolve(
            new HashSet<string>(TenantPermissions.SeedRolePermissions[role]),
            AuthType.SessionCookie,
            new HashSet<string>());

        foreach (var (controller, action) in GatedWriteActions())
        {
            Evaluate(controller, action, authenticated: true, scopes)
                .Should().BeNull($"the {role} role holds alerts.readwrite, so it must keep {controller.Name}.{action.Name}");
        }
    }

    /// <summary>
    /// The read-only seed roles keep both excursion-acknowledgement paths and no other
    /// alert-surface write. Neither holds <c>alerts.readwrite</c> — Clinician holds
    /// <c>alerts.read</c>, Viewer no alert scope at all — so that gate denies them everywhere. But
    /// both list <see cref="OAuthScopes.DeviceNotify"/> outright in
    /// <see cref="TenantPermissions.SeedRolePermissions"/> (and <see cref="MemberScopeResolver"/>
    /// would grant it anyway, as it gives <see cref="TenantPermissions.MemberPersonalScopes"/> to
    /// any member holding at least one permission), so the <c>device.notify</c> arm of each
    /// <see cref="DeviceCapabilityActions"/> gate admits them. This pins that matrix: a change that
    /// leaves a Clinician or a Viewer unable to stop an alert they are being shown, or that widens
    /// any other alert write to them, fails here.
    /// </summary>
    /// <remarks>
    /// The read-state actions on <c>NotificationsController</c> are not in
    /// <see cref="GatedWriteActions"/> — they carry no scope gate at all, for the reason recorded in
    /// <see cref="Ungated"/>, so these roles reach them too.
    /// </remarks>
    [Theory]
    [InlineData(TenantPermissions.SeedRoles.Clinician)]
    [InlineData(TenantPermissions.SeedRoles.Viewer)]
    public void ReadOnlySeedRole_KeepsBothExcursionAckPathsAndNoOtherAlertSurfaceWrite(string role)
    {
        var permissions = new HashSet<string>(TenantPermissions.SeedRolePermissions[role]);
        var scopes = MemberScopeResolver.Resolve(permissions, AuthType.SessionCookie, new HashSet<string>());

        scopes.Should().NotContain(OAuthScopes.AlertsReadWrite);
        scopes.Should().NotContain(OAuthScopes.FullAccess);
        permissions.Should().Contain(OAuthScopes.DeviceNotify,
            $"the {role} seed role lists device.notify in its own permission set");
        scopes.Should().Contain(OAuthScopes.DeviceNotify);

        foreach (var (controller, action) in GatedWriteActions())
        {
            var key = $"{controller.Name}.{action.Name}";
            var result = Evaluate(controller, action, authenticated: true, scopes);

            if (DeviceCapabilityActions.Contains(key, StringComparer.Ordinal))
            {
                result.Should().BeNull(
                    $"a {role} member holds device.notify, which {key} accepts alongside alerts.readwrite");
            }
            else
            {
                result.Should().BeOfType<ForbidResult>(
                    $"the {role} role holds no alerts.readwrite, so it must not reach {key}");
            }
        }
    }

    [Fact]
    public void ReadOnlyGuestSession_IsDeniedEveryAlertSurfaceWrite()
    {
        // The widest grant a guest link can hold: ValidateGrantScopes caps a guest at
        // AllowedGuestScopes, which is read-only and includes alerts.read. MemberScopeMiddleware
        // publishes Normalize() of exactly that list for AuthType.Guest — no membership widening.
        var stored = OAuthScopes.ValidateGrantScopes(
            OAuthScopes.AllowedGuestScopes, OAuthScopes.GrantTypeGuest);
        var guestScopes = OAuthScopes.Normalize(stored);

        guestScopes.Should().Contain(OAuthScopes.AlertsRead);
        guestScopes.Should().NotContain(OAuthScopes.AlertsReadWrite);
        guestScopes.Should().NotContain(OAuthScopes.DeviceNotify);

        foreach (var (controller, action) in GatedWriteActions())
        {
            Evaluate(controller, action, authenticated: true, guestScopes)
                .Should().BeOfType<ForbidResult>(
                    $"a read-only guest session must not reach {controller.Name}.{action.Name}");
        }
    }

    [Fact]
    public void UnauthenticatedCaller_IsDeniedEveryAlertSurfaceWrite()
    {
        foreach (var (controller, action) in GatedWriteActions())
        {
            Evaluate(controller, action, authenticated: false, new HashSet<string> { OAuthScopes.AlertsReadWrite })
                .Should().BeOfType<UnauthorizedResult>(
                    $"{controller.Name}.{action.Name} must reject an unauthenticated caller");
        }
    }

    [Fact]
    public void WildcardGrant_KeepsEveryAlertSurfaceWrite()
    {
        // A legacy api-secret and an instance-key service credential both normalise to "*", and
        // production direct grants carry it too.
        var wildcard = new HashSet<string> { OAuthScopes.FullAccess };

        OAuthScopes.SatisfiesScope(wildcard, OAuthScopes.AlertsReadWrite).Should().BeTrue();

        foreach (var (controller, action) in GatedWriteActions())
        {
            Evaluate(controller, action, authenticated: true, wildcard)
                .Should().BeNull($"a full-access grant must keep {controller.Name}.{action.Name}");
        }
    }

    [Fact]
    public void CompanionDeviceGrant_KeepsTheExcursionAckPathsAndNothingElse()
    {
        // Even a tenant OWNER's companion token resolves without alerts.readwrite: an OAuth access
        // token is a scoped credential, so membership is intersected with the grant rather than
        // replacing it, and the grant never asked for an alert scope.
        var companionScopes = MemberScopeResolver.Resolve(
            new HashSet<string>(TenantPermissions.SeedRolePermissions[TenantPermissions.SeedRoles.Owner]),
            AuthType.OAuthAccessToken,
            OAuthScopes.Normalize(CompanionGrantScopes).ToHashSet());

        companionScopes.Should().Contain(OAuthScopes.DeviceNotify);
        companionScopes.Should().NotContain(OAuthScopes.AlertsReadWrite);
        companionScopes.Should().NotContain(OAuthScopes.FullAccess);

        foreach (var (controller, action) in GatedWriteActions())
        {
            var key = $"{controller.Name}.{action.Name}";
            var result = Evaluate(controller, action, authenticated: true, companionScopes);

            if (DeviceCapabilityActions.Contains(key, StringComparer.Ordinal))
            {
                result.Should().BeNull(
                    $"the Acknowledge action posts to {key} with the device's own grant");
            }
            else
            {
                result.Should().BeOfType<ForbidResult>(
                    $"a device capability grant must not reach {key}");
            }
        }
    }

    [Fact]
    public void OnlyTheExcursionAckPaths_AcceptADeviceCapabilityScope()
    {
        var accepting = GatedWriteActions()
            .Where(x => RequiredScopes(x.Controller, x.Action)
                .Any(TenantPermissions.MemberPersonalScopes.Contains))
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .ToList();

        accepting.Should().BeEquivalentTo(DeviceCapabilityActions,
            "device.notify / device.actuate are held by every member with any permission, so "
            + "accepting one widens the gate to a read-only member — only the two excursion "
            + "acknowledgement paths may do so, and ExecuteAction narrows its arm to an "
            + "alert.firing notification in the action body");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static Assembly ApiAssembly => typeof(RequireScopeAttribute).Assembly;

    private static IEnumerable<Type> MonitoringControllers() =>
        ApiAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && typeof(ControllerBase).IsAssignableFrom(t)
                        && t.Namespace == MonitoringNamespace)
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    /// <summary>Every alert-surface write action that is expected to carry a gate.</summary>
    private static IEnumerable<(Type Controller, MethodInfo Action)> GatedWriteActions() =>
        MonitoringControllers()
            .SelectMany(c => WriteActions(c).Select(a => (Controller: c, Action: a)))
            .Where(x => !Ungated.ContainsKey($"{x.Controller.Name}.{x.Action.Name}"));

    private static IEnumerable<MethodInfo> WriteActions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(action => HttpVerbs(action).Overlaps(WriteVerbs))
            .OrderBy(a => a.Name, StringComparer.Ordinal);

    private static HashSet<string> HttpVerbs(MethodInfo action) =>
        action.GetCustomAttributes(inherit: true)
            .OfType<IActionHttpMethodProvider>()
            .SelectMany(a => a.HttpMethods)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The scopes an action's gate requires: an explicit <see cref="RequireScopeAttribute"/>, or the
    /// controller's <see cref="IWriteScopedController.WriteScope"/> when it defers to
    /// <see cref="RequireDeclaredWriteScopeAttribute"/>. Empty when the action carries neither.
    /// </summary>
    private static IEnumerable<string> RequiredScopes(Type controller, MethodInfo action)
    {
        var attributes = action.GetCustomAttributes(inherit: true);

        foreach (var scope in attributes.OfType<RequireScopeAttribute>().SelectMany(a => a.Scopes))
            yield return scope;

        if (attributes.Any(a => a is RequireDeclaredWriteScopeAttribute)
            && ControllerInstance(controller) is IWriteScopedController declared)
            yield return declared.WriteScope;
    }

    /// <summary>
    /// Runs the gate an action actually declares, in MVC's order: the authorization filters first,
    /// then the action filters (<see cref="RequireDeclaredWriteScopeAttribute"/> is one). Returns
    /// the short-circuit result, or null when the request would proceed to the handler.
    /// </summary>
    private static IActionResult? Evaluate(
        Type controller, MethodInfo action, bool authenticated, IReadOnlySet<string> grantedScopes)
    {
        var httpContext = new DefaultHttpContext();
        if (authenticated)
            httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true };
        httpContext.Items["GrantedScopes"] = grantedScopes;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filters = action.GetCustomAttributes(inherit: true);

        foreach (var filter in filters.OfType<IAuthorizationFilter>())
        {
            var authorizationContext = new AuthorizationFilterContext(actionContext, []);
            filter.OnAuthorization(authorizationContext);
            if (authorizationContext.Result is not null)
                return authorizationContext.Result;
        }

        var executingContext = new ActionExecutingContext(
            actionContext, [], new Dictionary<string, object?>(), ControllerInstance(controller));

        foreach (var filter in filters.OfType<IActionFilter>())
        {
            filter.OnActionExecuting(executingContext);
            if (executingContext.Result is not null)
                return executingContext.Result;
        }

        return null;
    }

    /// <summary>
    /// A controller instance for reading <see cref="IWriteScopedController.WriteScope"/> and for
    /// running the write-scope filters. The getter returns a constant and the filters touch nothing
    /// else, so the controller is left unconstructed — these controllers take services with no
    /// mockable constructor.
    /// </summary>
    private static object ControllerInstance(Type controller) =>
        RuntimeHelpers.GetUninitializedObject(controller);
}
