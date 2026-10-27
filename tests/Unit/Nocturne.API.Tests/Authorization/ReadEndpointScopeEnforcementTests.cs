using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Nocturne.API.Attributes;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Guards the per-action authorization invariant for the V1/V2/V3 (Nightscout-compat) and V4 READ
/// planes, the mirror of <see cref="WriteEndpointScopeEnforcementTests"/> and
/// <see cref="V4WriteScopeGatingTests"/>.
///
/// The controller-level <c>HasPermissions</c> policy is the same object as the global
/// <c>FallbackPolicy</c>, and it succeeds on any non-empty <c>PermissionTrie</c>. So without a
/// per-action <see cref="RequireScopeAttribute"/> a grant scoped to one category reads every
/// category: a <c>sleep.read</c> token could GET glucose from <c>/api/v1/entries</c> and basal
/// rates, carb ratios and ISF from <c>/api/v1/profile</c>. This test fails if a new read endpoint
/// ships without per-action authorization. The V4 plane carries the same rule with its own
/// exemption lists.
/// </summary>
public class ReadEndpointScopeEnforcementTests
{
    private static readonly string[] ReadVerbs = ["GET", "HEAD"];

    /// <summary>
    /// Controllers exempt from the per-action RequireScope rule, with justification.
    /// </summary>
    /// <remarks>
    /// Deliberately minimal. Controllers carrying <c>[AllowAnonymous]</c> (V1 <c>StatusController</c>,
    /// <c>VersionsController</c>, <c>VersionController</c>, V1 <c>AuthenticationController</c>)
    /// and write-only controllers (<c>AlexaController</c>, a read
    /// exposed over POST that runs its own permission check) are skipped by the scan itself, so
    /// listing them here would only create a way for a later change to silently lose its gate.
    /// <see cref="ExemptControllers_AreStillNeeded"/> guards that.
    /// </remarks>
    private static readonly HashSet<string> ExemptControllers = new()
    {
        // Self-introspection only. Class-level [Authorize] already requires an authenticated
        // caller, and GetAllPermissions/GetPermissionTrie describe that caller's OWN grants — there
        // is no cross-category tenant read to scope. Its subject/role management actions each carry
        // [RequireSuperuser], and its token-exchange action is explicitly [AllowAnonymous].
        "AuthorizationController",
    };

    /// <summary>
    /// Read actions exempt from the rule, keyed <c>Controller.Action</c> with the mechanism that
    /// governs them instead. Action-granular so exempting one action cannot exempt a controller's
    /// whole read surface. <see cref="ExemptReadActions_NameALiveReadAction"/> keeps it honest.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ExemptReadActions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // NotificationV1Service.GetAdminNotifiesAsync checks *:*:admin itself and returns the
            // notification bodies only to an admin, otherwise just notifyCount. A scope gate would
            // turn that documented degradation into a 403, and site notices are not a data category.
            ["NotificationsController.GetAdminNotifies"] = "service authorizes internally and degrades",

            // The collection is a route or query value, so an attribute here would be an OR across
            // every collection the route serves and would admit a caller holding one category to
            // all of them. LegacyStorageReadScopes resolves the governing scope per request inside
            // the action instead. LegacyStorageReadScopeTests covers the mapping and
            // TimeQueryStorageGateTests drives the actions.
            ["TimeQueryController.GetTimeQueryEcho"] = PerRequestStorageScope,
            ["TimeQueryController.GetTimeQueryEchoWithPrefix"] = PerRequestStorageScope,
            ["TimeQueryController.GetTimeQueryEchoWithPrefixAndRegex"] = PerRequestStorageScope,
            ["TimeQueryController.GetSlicedData"] = PerRequestStorageScope,
            ["TimeQueryController.GetSlicedDataWithType"] = PerRequestStorageScope,
            ["TimeQueryController.GetSlicedDataWithTypeAndPrefix"] = PerRequestStorageScope,
            ["TimeQueryController.GetSlicedDataWithAll"] = PerRequestStorageScope,
            ["CountController.CountGeneric"] = PerRequestStorageScope,

            // Merges four categories into one number, so it cannot be filtered and requires every
            // category's read scope. Read from ActivityReadScopeGuard.AdmissionScopes in the action.
            ["CountController.CountActivity"] = "every activity category, checked in the action",

            // Every caller with a credential may ask; the answer lists only the collections its
            // scopes read, through LegacyStorageReadScopes in StatusService.
            ["StatusController.GetStatus"] = CallerNarrowedAnswer,
            ["LastModifiedController.GetLastModified"] = CallerNarrowedAnswer,
        };

    private const string PerRequestStorageScope = "storage-derived scope, checked in the action";

    private const string CallerNarrowedAnswer = "answer narrowed to the caller's read scopes";

    private const string V4Namespace = "Nocturne.API.Controllers.V4";

    private static class V4Reason
    {
        /// <summary>Rows keyed by the caller's own subject id or memberships; nothing another member holds is read.</summary>
        public const string OwnData = "caller's own data, keyed by their subject";

        /// <summary>The action, or the service it calls, checks the scope or the share and guest access itself and answers Forbid, NotFound or a narrowed result.</summary>
        public const string ActionChecksAccess = "scope or share access checked in the action or its service";

        /// <summary>The tenant's trackers, read whole by a member holding alerts.read and not at all by anyone else (see TrackerReadAccess).</summary>
        public const string TenantTrackers = "tenant's trackers, alerts.read checked in the action";

        /// <summary>Instance-level or compiled-in reference data; no tenant row is read.</summary>
        public const string StaticReference = "static reference data, no tenant rows";

        /// <summary>Answers 501 and stores nothing.</summary>
        public const string NotStored = "answers 501, stores nothing";

        /// <summary>The row is found by a bearer token in the route, so possessing the token is the control.</summary>
        public const string CapabilityToken = "row found by a capability token in the route";

        /// <summary>Every caller may ask; the action drops or refuses the parts the caller's scopes do not read.</summary>
        public const string CallerNarrowedAnswer = "answer narrowed to the caller's read scopes in the action";
    }

    private static IReadOnlyDictionary<string, string> V4ExemptControllers => V4ScopeGuardExemptions.Controllers;

    private static readonly IReadOnlyDictionary<string, string> V4ExemptReadActions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CoachMarkController.GetAll"] = V4Reason.OwnData,
            ["ClockFacesController.List"] = V4Reason.OwnData,
            ["ClockFacesController.GetStarter"] = V4Reason.OwnData,
            ["UserPreferencesController.GetPreferences"] = V4Reason.OwnData,
            ["TranslationsController.GetDrafts"] = V4Reason.OwnData,
            ["ConnectedAppsController.List"] = V4Reason.OwnData,
            ["SessionsController.List"] = V4Reason.OwnData,
            ["GuestLinkController.GetGuestLinks"] = V4Reason.OwnData,
            ["ChatIdentityController.GetLinks"] = V4Reason.OwnData,
            ["ChatIdentityController.GetPending"] = V4Reason.CapabilityToken,
            ["BackupSignInPromptController.GetBackupSignInPrompt"] = V4Reason.OwnData,
            ["MembershipRequestController.GetMyRequest"] = V4Reason.OwnData,
            ["MyPermissionsController.GetMyPermissions"] = V4Reason.OwnData,
            ["MyTenantsController.GetMyTenants"] = V4Reason.OwnData,
            ["MyTenantsController.GetOverview"] = V4Reason.ActionChecksAccess,
            ["PlatformController.GetTenants"] = V4Reason.OwnData,
            ["FoodsController.GetFavorites"] = V4Reason.OwnData,
            ["NotificationsController.GetNotifications"] = V4Reason.OwnData,
            ["TrackersController.GetPresets"] = V4Reason.TenantTrackers,

            ["MemberInviteController.ListInvites"] = V4Reason.ActionChecksAccess,
            ["MemberInviteController.GetEffectivePermissions"] = V4Reason.ActionChecksAccess,
            ["MemberRecoveryController.GetLockedOutMembers"] = V4Reason.ActionChecksAccess,
            ["MembershipRequestController.GetPendingRequests"] = V4Reason.ActionChecksAccess,
            ["MembershipRequestController.GetMembershipRequestSettings"] = V4Reason.ActionChecksAccess,
            ["RoleController.GetRoles"] = V4Reason.ActionChecksAccess,
            ["ShareLinkController.GetShareLink"] = V4Reason.ActionChecksAccess,
            ["GuestLinkController.GetGuestLinkScopeOptions"] = V4Reason.ActionChecksAccess,
            ["TenantSettingsController.GetTenantSettings"] = V4Reason.ActionChecksAccess,
            ["ReleasesController.GetReleaseStatus"] = V4Reason.ActionChecksAccess,
            ["AuditController.GetMutationAuditLog"] = V4Reason.ActionChecksAccess,
            ["AuditController.GetReadAccessAuditLog"] = V4Reason.ActionChecksAccess,
            ["AuditController.GetAuditConfig"] = V4Reason.ActionChecksAccess,
            ["TenantAppearanceController.GetTenantAppearance"] = V4Reason.ActionChecksAccess,

            ["TrackersController.GetDefinitions"] = V4Reason.TenantTrackers,
            ["TrackersController.GetDefinition"] = V4Reason.TenantTrackers,
            ["TrackersController.GetActiveInstances"] = V4Reason.TenantTrackers,
            ["TrackersController.GetInstanceHistory"] = V4Reason.TenantTrackers,
            ["TrackersController.GetUpcomingInstances"] = V4Reason.TenantTrackers,
            ["TrackerLegacyFallbacksController.GetLegacyFallbacks"] = V4Reason.ActionChecksAccess,

            ["DeviceCatalogController.GetCatalog"] = V4Reason.StaticReference,
            ["DeviceCatalogController.GetCatalogByCategory"] = V4Reason.StaticReference,
            ["InsulinCatalogController.GetCatalog"] = V4Reason.StaticReference,
            ["InsulinCatalogController.GetCatalogByCategory"] = V4Reason.StaticReference,
            ["ClientDevicesController.GetCapabilityCatalog"] = V4Reason.StaticReference,
            ["TranslationsController.GetCatalogSource"] = V4Reason.StaticReference,
            ["PlatformController.GetTransitionStatus"] = V4Reason.StaticReference,
            ["SystemController.GetChannelStatuses"] = V4Reason.StaticReference,
            ["TrackersController.GetTriggerEventTypes"] = V4Reason.StaticReference,
            ["TrackersController.GetEndEventTypes"] = V4Reason.StaticReference,
            ["ServicesController.GetUploaderApps"] = V4Reason.StaticReference,
            ["ServicesController.GetUploaderSetup"] = V4Reason.StaticReference,
            ["ServicesController.GetConnectorCapabilities"] = V4Reason.StaticReference,
            ["ServicesController.GetApiInfo"] = V4Reason.StaticReference,
            ["SupportController.GetFallbackUrl"] = V4Reason.StaticReference,

            ["DatabaseCapacityController.Get"] = V4ScopeGuardExemptions.PlatformAdminRole,

            ["WebhookSettingsController.GetWebhookSettings"] = V4Reason.NotStored,

            // UISettingsSection.ReadScope per section; StateSpanReadScopeGuard per category.
            ["UISettingsController.GetUISettings"] = V4Reason.CallerNarrowedAnswer,
            ["UISettingsController.GetSectionSettings"] = V4Reason.CallerNarrowedAnswer,
            ["StateSpansController.GetStateSpans"] = V4Reason.CallerNarrowedAnswer,
            ["StateSpansController.GetStateSpan"] = V4Reason.CallerNarrowedAnswer,
        };

    [Fact]
    public void EveryV1V2AndV3ReadAction_RequiresAnOAuthScope()
    {
        var actions = ReadActions(IsV1ToV3, ExemptControllers).ToList();

        // Sanity: the scan must actually discover the read surface, otherwise the assertion
        // below would pass vacuously if the reflection query silently matched nothing.
        actions.Should().HaveCountGreaterThan(50,
            "the reflection scan should discover the V1/V2/V3 read endpoints");

        var violations = actions
            .Where(a => !HasReadGate(a.Controller, a.Action)
                        && !ExemptReadActions.ContainsKey($"{a.Controller.Name}.{a.Action.Name}"))
            .Select(a => $"{a.Controller.Name}.{a.Action.Name} [{string.Join(",", a.Verbs)}]")
            .ToList();

        violations.Should().BeEmpty(
            "every V1/V2/V3 read endpoint must carry [RequireScope] or [RequirePermission] so a " +
            "grant scoped to one data category cannot read every other category. " +
            "Unprotected: " + string.Join("; ", violations));
    }

    /// <summary>
    /// The V4 plane has the same hole: a controller-level <c>[Authorize]</c> admits every
    /// authenticated credential, so a read action with no scope lets a <c>glucose.read</c> key read
    /// whatever it returns. A V4 read action with no gate must be named in
    /// <see cref="V4ExemptReadActions"/> with the mechanism that governs it instead.
    /// </summary>
    [Fact]
    public void EveryV4ReadAction_RequiresAnOAuthScope()
    {
        var actions = ReadActions(IsV4, new HashSet<string>(V4ExemptControllers.Keys)).ToList();

        actions.Should().HaveCountGreaterThan(150,
            "the reflection scan should discover the V4 read endpoints");

        var violations = actions
            .Where(a => !HasReadGate(a.Controller, a.Action)
                        && !V4ExemptReadActions.ContainsKey($"{a.Controller.Name}.{a.Action.Name}"))
            .Select(a => $"{a.Controller.Name}.{a.Action.Name} [{string.Join(",", a.Verbs)}]")
            .ToList();

        violations.Should().BeEmpty(
            "every V4 read endpoint must carry [RequireScope] or [RequirePermission], or be named in " +
            "V4ExemptReadActions with the mechanism that governs it. Unprotected: " +
            string.Join("; ", violations));
    }

    /// <summary>
    /// Guards the allow-list itself: an entry that no longer needs to be there would silently
    /// exempt a controller that had lost its gate.
    /// </summary>
    [Fact]
    public void ExemptControllers_AreStillNeeded()
    {
        var assembly = typeof(RequireScopeAttribute).Assembly;

        foreach (var name in ExemptControllers)
        {
            var type = assembly.GetTypes().SingleOrDefault(t => t.Name == name && IsV1ToV3(t));

            type.Should().NotBeNull($"{name} is exempted but no longer exists in V1/V2/V3");
            type!.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull(
                $"{name} is [AllowAnonymous], which the scan already skips, remove the exemption");

            ReadActions(t => t == type)
                .Where(a => !HasReadGate(a.Controller, a.Action))
                .Should().NotBeEmpty(
                    $"{name} now gates every read action, so its exemption is stale, remove it");
        }
    }

    /// <summary>
    /// Public share links reach exactly this read surface, and their scopes are narrowed to
    /// <see cref="Nocturne.Core.Models.Authorization.Scope.PublicShareScopes"/>. A read
    /// scope outside that set is not a bug — the share RLS policies hide the underlying tables from
    /// shares anyway (see <c>ShareDataCategories</c>) — but the scopes the share view depends on
    /// must stay inside it, or every share link 403s.
    /// </summary>
    [Fact]
    public void ShareReachableReadScopes_StayWithinPublicShareScopes()
    {
        var shareable = Nocturne.Core.Models.Authorization.Scope.PublicShareScopes;

        shareable.Should().Contain(Nocturne.Core.Models.Authorization.Scope.GlucoseRead,
            "the default share grant is glucose-only, so the glucose read endpoints " +
            "(/api/v1/entries, /pebble, /api/v2/properties, ddata, summary) must be satisfiable by it");
        shareable.Should().Contain(Nocturne.Core.Models.Authorization.Scope.TreatmentsRead);
        shareable.Should().Contain(Nocturne.Core.Models.Authorization.Scope.DevicesRead);
        shareable.Should().Contain(Nocturne.Core.Models.Authorization.Scope.FoodRead);

        shareable.Should().OnlyContain(s => s.EndsWith(".read", StringComparison.Ordinal),
            "RequireScope admits the anonymous share principal on its scope set alone, so the set " +
            "must never contain a write scope");
    }

    [Fact]
    public void ExemptReadActions_NameALiveReadAction() =>
        AssertNamesLiveUngatedReadActions(ExemptReadActions.Keys, IsV1ToV3, "V1/V2/V3");

    [Fact]
    public void V4ExemptReadActions_NameALiveReadAction() =>
        AssertNamesLiveUngatedReadActions(V4ExemptReadActions.Keys, IsV4, "V4");

    [Fact]
    public void V4ExemptControllers_AreStillNeeded()
    {
        foreach (var name in V4ExemptControllers.Keys)
        {
            var type = typeof(RequireScopeAttribute).Assembly.GetTypes()
                .SingleOrDefault(t => t.Name == name && IsV4(t));

            type.Should().NotBeNull($"{name} is exempted but no longer exists in V4");
            type!.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull(
                $"{name} is [AllowAnonymous], which the scan already skips, remove the exemption");

            ReadActions(t => t == type)
                .Where(a => !HasReadGate(a.Controller, a.Action))
                .Should().NotBeEmpty(
                    $"{name} now gates every read action, so its exemption is stale, remove it");
        }
    }

    [Fact]
    public void V4ExemptControllers_CarryTheMechanismTheyClaim()
    {
        var assembly = typeof(RequireScopeAttribute).Assembly;

        foreach (var (name, reason) in V4ExemptControllers)
        {
            var attributes = assembly.GetTypes()
                .Single(t => t.Name == name && IsV4(t))
                .GetCustomAttributes(inherit: true);

            V4ScopeGuardExemptions.Carries(attributes, reason)
                .Should().BeTrue($"{name} claims '{reason}' but does not carry it");
        }
    }

    private static void AssertNamesLiveUngatedReadActions(
        IEnumerable<string> keys, Func<Type, bool> inPlane, string plane)
    {
        foreach (var key in keys)
        {
            var parts = key.Split('.', 2);

            // Resolved by the type that declares the action: the same controller name exists in more
            // than one version namespace (NotificationsController in V1 and V2). An [AllowAnonymous]
            // controller is skipped by the scan, so no exemption can name it (V1 StatusController).
            var matches = ReadActions(inPlane)
                .Where(a => a.Controller.Name == parts[0] && a.Action.Name == parts[1])
                .ToList();

            matches.Should().HaveCount(1, $"{key} must name exactly one live {plane} read action");

            HasReadGate(matches[0].Controller, matches[0].Action)
                .Should().BeFalse($"{key} now carries a gate, so its exemption is stale, remove it");
        }
    }

    private static bool IsV1ToV3(Type type) =>
        type.Namespace is "Nocturne.API.Controllers.V1"
            or "Nocturne.API.Controllers.V2" or "Nocturne.API.Controllers.V3";

    private static bool IsV4(Type type) =>
        type.Namespace == V4Namespace
        || type.Namespace?.StartsWith(V4Namespace + ".", StringComparison.Ordinal) == true;

    private static bool HasReadGate(Type controller, MethodInfo action) =>
        action.GetCustomAttribute<RequireScopeAttribute>() != null
        || action.GetCustomAttribute<RequirePermissionAttribute>() != null
        || controller.GetCustomAttribute<RequireScopeAttribute>() != null
        || controller.GetCustomAttribute<RequirePermissionAttribute>() != null;

    /// <summary>
    /// Every read action of the controllers in a plane that is not anonymous. Includes actions
    /// inherited from a V4 base controller, which carry no attribute of their own and are gated, if
    /// at all, on the concrete controller.
    /// </summary>
    private static IEnumerable<(Type Controller, MethodInfo Action, HashSet<string> Verbs)> ReadActions(
        Func<Type, bool> inPlane, IReadOnlySet<string>? exemptControllers = null)
    {
        foreach (var type in typeof(RequireScopeAttribute).Assembly.GetTypes())
        {
            if (!inPlane(type) || type.IsAbstract || !typeof(ControllerBase).IsAssignableFrom(type))
                continue;
            if (exemptControllers?.Contains(type.Name) == true)
                continue;
            if (type.GetCustomAttribute<AllowAnonymousAttribute>() != null)
                continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var verbs = method.GetCustomAttributes<HttpMethodAttribute>()
                    .SelectMany(a => a.HttpMethods)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!verbs.Overlaps(ReadVerbs))
                    continue;

                if (method.GetCustomAttribute<AllowAnonymousAttribute>() != null)
                    continue;

                yield return (type, method, verbs);
            }
        }
    }
}
