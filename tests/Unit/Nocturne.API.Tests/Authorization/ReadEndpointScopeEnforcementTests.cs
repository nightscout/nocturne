using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Nocturne.API.Attributes;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Guards the per-action authorization invariant for the V1/V2/V3 (Nightscout-compat) READ plane,
/// the mirror of <see cref="WriteEndpointScopeEnforcementTests"/>.
///
/// The controller-level <c>HasPermissions</c> policy is the same object as the global
/// <c>FallbackPolicy</c>, and it succeeds on any non-empty <c>PermissionTrie</c>. So without a
/// per-action <see cref="RequireScopeAttribute"/> a grant scoped to one category reads every
/// category: a <c>sleep.read</c> token could GET glucose from <c>/api/v1/entries</c> and basal
/// rates, carb ratios and ISF from <c>/api/v1/profile</c>. This test fails if a new read endpoint
/// ships without per-action authorization.
/// </summary>
public class ReadEndpointScopeEnforcementTests
{
    private static readonly string[] ReadVerbs = ["GET", "HEAD"];

    /// <summary>
    /// Controllers exempt from the per-action RequireScope rule, with justification.
    /// </summary>
    /// <remarks>
    /// Deliberately minimal. Controllers carrying <c>[AllowAnonymous]</c> (V1/V3 <c>StatusController</c>,
    /// <c>VersionsController</c>, <c>VersionController</c>, V1 <c>AuthenticationController</c>,
    /// V3 <c>LastModifiedController</c>) and write-only controllers (<c>AlexaController</c>, a read
    /// exposed over POST that runs its own permission check) are skipped by the scan itself, so
    /// listing them here would only create a way for a later change to silently lose its gate.
    /// <see cref="ExemptControllers_AreStillNeeded"/> guards that.
    /// </remarks>
    private static readonly HashSet<string> ExemptControllers = new()
    {
        // Self-introspection only. Class-level [Authorize] already requires an authenticated
        // caller, and GetAllPermissions/GetPermissionTrie describe that caller's OWN grants — there
        // is no cross-category tenant read to scope. Its subject/role management actions each carry
        // [RequireAdmin], and its token-exchange action is explicitly [AllowAnonymous].
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
        };

    private const string PerRequestStorageScope = "storage-derived scope, checked in the action";

    [Fact]
    public void EveryV1V2AndV3ReadAction_RequiresAnOAuthScope()
    {
        var assembly = typeof(RequireScopeAttribute).Assembly;
        var violations = new List<string>();
        var readActionsChecked = 0;

        foreach (var type in assembly.GetTypes())
        {
            if (type.Namespace is not ("Nocturne.API.Controllers.V1"
                or "Nocturne.API.Controllers.V2"
                or "Nocturne.API.Controllers.V3"))
                continue;
            if (type.IsAbstract || !typeof(ControllerBase).IsAssignableFrom(type))
                continue;
            if (ExemptControllers.Contains(type.Name))
                continue;
            if (type.GetCustomAttribute<AllowAnonymousAttribute>() != null)
                continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var verbs = method.GetCustomAttributes<HttpMethodAttribute>()
                    .SelectMany(a => a.HttpMethods)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!verbs.Overlaps(ReadVerbs))
                    continue;

                // Endpoints deliberately marked [AllowAnonymous] are intentionally public.
                if (method.GetCustomAttribute<AllowAnonymousAttribute>() != null)
                    continue;

                readActionsChecked++;

                var hasPerActionAuthz =
                    method.GetCustomAttribute<RequireScopeAttribute>() != null
                    || method.GetCustomAttribute<RequirePermissionAttribute>() != null
                    || type.GetCustomAttribute<RequireScopeAttribute>() != null
                    || type.GetCustomAttribute<RequirePermissionAttribute>() != null;

                if (!hasPerActionAuthz && !ExemptReadActions.ContainsKey($"{type.Name}.{method.Name}"))
                    violations.Add($"{type.Name}.{method.Name} [{string.Join(",", verbs)}]");
            }
        }

        // Sanity: the scan must actually discover the read surface, otherwise the assertion
        // below would pass vacuously if the reflection query silently matched nothing.
        readActionsChecked.Should().BeGreaterThan(50,
            "the reflection scan should discover the V1/V2/V3 read endpoints");

        violations.Should().BeEmpty(
            "every V1/V2/V3 read endpoint must carry [RequireScope] or [RequirePermission] so a " +
            "grant scoped to one data category cannot read every other category. " +
            "Unprotected: " + string.Join("; ", violations));
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
            var type = assembly.GetTypes().SingleOrDefault(t =>
                t.Name == name
                && t.Namespace is "Nocturne.API.Controllers.V1"
                    or "Nocturne.API.Controllers.V2" or "Nocturne.API.Controllers.V3");

            type.Should().NotBeNull($"{name} is exempted but no longer exists in V1/V2/V3");
            type!.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull(
                $"{name} is [AllowAnonymous], which the scan already skips — remove the exemption");

            var ungatedReads = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>()
                    .SelectMany(a => a.HttpMethods)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .Overlaps(ReadVerbs))
                .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() == null)
                .Where(m => m.GetCustomAttribute<RequireScopeAttribute>() == null
                            && m.GetCustomAttribute<RequirePermissionAttribute>() == null
                            && type.GetCustomAttribute<RequireScopeAttribute>() == null
                            && type.GetCustomAttribute<RequirePermissionAttribute>() == null)
                .ToList();

            ungatedReads.Should().NotBeEmpty(
                $"{name} now gates every read action, so its exemption is stale — remove it");
        }
    }

    /// <summary>
    /// Public share links reach exactly this read surface, and their scopes are narrowed to
    /// <see cref="Nocturne.Core.Models.Authorization.TenantPermissions.PublicShareScopes"/>. A read
    /// scope outside that set is not a bug — the share RLS policies hide the underlying tables from
    /// shares anyway (see <c>ShareDataCategories</c>) — but the scopes the share view depends on
    /// must stay inside it, or every share link 403s.
    /// </summary>
    [Fact]
    public void ShareReachableReadScopes_StayWithinPublicShareScopes()
    {
        var shareable = Nocturne.Core.Models.Authorization.TenantPermissions.PublicShareScopes;

        shareable.Should().Contain(Nocturne.Core.Models.Authorization.OAuthScopes.GlucoseRead,
            "the default share grant is glucose-only, so the glucose read endpoints " +
            "(/api/v1/entries, /pebble, /api/v2/properties, ddata, summary) must be satisfiable by it");
        shareable.Should().Contain(Nocturne.Core.Models.Authorization.OAuthScopes.TreatmentsRead);
        shareable.Should().Contain(Nocturne.Core.Models.Authorization.OAuthScopes.DevicesRead);
        shareable.Should().Contain(Nocturne.Core.Models.Authorization.OAuthScopes.FoodRead);

        shareable.Should().OnlyContain(s => s.EndsWith(".read", StringComparison.Ordinal),
            "RequireScope admits the anonymous share principal on its scope set alone, so the set " +
            "must never contain a write scope");
    }

    [Fact]
    public void ExemptReadActions_NameALiveReadAction()
    {
        var assembly = typeof(RequireScopeAttribute).Assembly;

        foreach (var (key, _) in ExemptReadActions)
        {
            var parts = key.Split('.', 2);

            // Resolved by the type that declares the action: the same controller name exists in more
            // than one version namespace (NotificationsController in V1 and V2).
            var matches = assembly.GetTypes()
                .Where(t => t.Name == parts[0]
                    && t.Namespace is "Nocturne.API.Controllers.V1"
                        or "Nocturne.API.Controllers.V2" or "Nocturne.API.Controllers.V3")
                .Select(t => (Type: t, Method: t
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == parts[1])))
                .Where(x => x.Method is not null)
                .ToList();

            matches.Should().HaveCount(1,
                $"{key} must name exactly one live V1/V2/V3 read action");
            var method = matches[0].Method!;

            method!.GetCustomAttributes<HttpMethodAttribute>()
                .SelectMany(a => a.HttpMethods)
                .Should().Contain(v => ReadVerbs.Contains(v),
                    $"{key} is exempted from the READ rule but is not a read action");

            (method.GetCustomAttribute<RequireScopeAttribute>() is null
                && method.GetCustomAttribute<RequirePermissionAttribute>() is null)
                .Should().BeTrue($"{key} now carries a gate, so its exemption is stale — remove it");
        }
    }
}