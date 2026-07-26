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
    private static readonly HashSet<string> ExemptControllers = new()
    {
        // Whole-controller [AllowAnonymous] compatibility surfaces that expose no tenant data:
        // Nightscout clients probe these before they hold any credential.
        "StatusController",        // V1 + V3 /status — server capabilities and settings echo
        "VersionsController",      // V1 /api/versions
        "VersionController",       // V3 /version
        "AuthenticationController",// V1 /verifyauth — reports whether the caller's own secret works
        "LastModifiedController",  // V3 /lastmodified — per-collection high-water marks

        // Self-introspection only: class-level [Authorize] means an authenticated caller, and the
        // responses describe that caller's OWN grants, so there is no cross-category read to gate.
        // Every subject/role management action on it already carries [RequireAdmin].
        "AuthorizationController",

        // Reflection cannot see the gate: the actions are authorized per-storage inside the
        // action body rather than by attribute.
        "AlexaController",        // read exposed over POST; performs its own permission check
    };

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

                if (!hasPerActionAuthz)
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
}
