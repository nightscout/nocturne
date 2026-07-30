using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Reflection guard asserting that every MVC controller action carries an explicit authorization
/// decision. An action passes when it (or its controller) has an <see cref="IAuthorizeData"/>
/// attribute (<c>[Authorize]</c> and friends), a Nocturne authorization filter
/// (<c>[RequireAdmin]</c>, <c>[RequireScope]</c>, <c>[RequireInstanceKeyAuth]</c>, …), or an
/// explicit <c>[AllowAnonymous]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="AuthorizationConfiguration.AddNocturneAuthorization"/> fallback policy
/// (<see cref="HasPermissionsRequirement"/>) gates every attribute-less endpoint by requiring a
/// non-empty <see cref="Nocturne.Core.Models.PermissionTrie"/>. That fallback rejects a bare
/// unauthenticated request (empty trie) but ADMITS the anonymous public-share subject, whose trie
/// carries the tenant's public read scopes. So relying on the fallback alone leaves a controller
/// reachable by any public-share visitor — acceptable for the deliberately share-facing read
/// surfaces (glucose/treatment analytics gated further by per-category share RLS), but not for
/// member/admin data or any write surface.
/// </para>
/// <para>
/// <see cref="FallbackGatedControllers"/> is the explicit, documented allow-list of controllers
/// that intentionally lean on the fallback policy (plus per-category share RLS and/or in-handler
/// permission checks) instead of an attribute. Every other controller action must declare its
/// gate, so a newly added controller that forgets one fails this test rather than shipping
/// silently reachable.
/// </para>
/// </remarks>
public class ControllerAuthorizationCoverageTests
{
    private static Assembly ApiAssembly => typeof(AuthorizationConfiguration).Assembly;

    /// <summary>
    /// Controllers whose actions are intentionally gated only by the <see cref="HasPermissionsRequirement"/>
    /// fallback policy (and, where they read PHI, per-category public-share RLS or in-handler
    /// <c>HasPermission</c> checks) rather than an authorization attribute. Keyed by full type name
    /// with the rationale as the value.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> FallbackGatedControllers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ── V4 public-share read analytics ──────────────────────────────────────────────
            // Read-only (GET) aggregates over glucose/treatment data that the public-share
            // dashboard renders for an anonymous share visitor. The fallback admits the share
            // subject; the per-category share RLS policy (ShareDataCategories) then restricts the
            // underlying rows to the categories the share was granted. Adding [Authorize] here
            // would 401 the anonymous share dashboard. All actions are GET, so there is no write
            // surface to expose.
            ["Nocturne.API.Controllers.V4.Analytics.ActogramController"] = "public-share read analytics (fallback + share RLS)",
            ["Nocturne.API.Controllers.V4.Analytics.ChartDataController"] = "public-share read analytics (fallback + share RLS)",
            ["Nocturne.API.Controllers.V4.Analytics.DataOverviewController"] = "public-share read analytics (fallback + share RLS)",
            ["Nocturne.API.Controllers.V4.Analytics.RetrospectiveController"] = "public-share read analytics (fallback + share RLS)",
            ["Nocturne.API.Controllers.V4.Analytics.PredictionController"] = "public-share read analytics (fallback + share RLS)",

            // ── Legacy Nightscout v1 surfaces (authorize via OAuth scope in the fallback trie) ──
            // v1 endpoints authenticate via the api-secret / token trie and are gated by the
            // fallback policy plus in-handler scope checks, matching upstream Nightscout behaviour.
            // They deliberately do not use the v4 [Authorize] convention.
            ["Nocturne.API.Controllers.V1.AlexaController"] = "legacy v1 (fallback trie + in-handler)",

            // Its routes take the collection as a route or query value, so an attribute would be an
            // OR across every collection they serve. LegacyStorageReadScopes resolves the governing
            // scope per request in the action; ReadEndpointScopeEnforcementTests records each action.
            ["Nocturne.API.Controllers.V1.TimeQueryController"] = "storage-derived scope, checked in the action",
            ["Nocturne.API.Controllers.V1.CountController"] = "storage-derived scope, checked in the action",

            // ── Authentication credential-management surfaces ──────────────────────────────────
            // These live in the Authentication namespace and mix explicit [AllowAnonymous] public
            // endpoints (OAuth/OIDC/login/discovery) with authenticated "manage my own
            // credentials/grants" endpoints that rely on the fallback trie plus in-handler subject
            // resolution. This is the pre-existing Authentication-namespace convention (unchanged
            // by the v4 authorization pass); the fallback still rejects a bare unauthenticated
            // caller (empty trie).
            ["Nocturne.API.Controllers.Authentication.OAuthController"] = "OAuth: public endpoints [AllowAnonymous], grant management via fallback trie",
            ["Nocturne.API.Controllers.Authentication.OidcController"] = "OIDC: discovery/login [AllowAnonymous], identity linking/management via fallback trie",
            ["Nocturne.API.Controllers.Authentication.PasskeyController"] = "Passkey: registration/login [AllowAnonymous], credential management via fallback trie",
            ["Nocturne.API.Controllers.Authentication.DirectGrantController"] = "manage-my-own API grants via fallback trie",
            ["Nocturne.API.Controllers.Authentication.TotpController"] = "TOTP: login [AllowAnonymous], enrolment/management via fallback trie",
        };

    [Fact]
    public void EveryControllerAction_HasAnExplicitAuthorizationDecision()
    {
        var controllers = ApiAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t)
                        && t is { IsClass: true, IsAbstract: false, IsPublic: true }
                        && t.GetCustomAttribute<NonControllerAttribute>() is null)
            .ToList();

        controllers.Should().NotBeEmpty("the API assembly must expose controllers to audit");

        var unprotected = new List<string>();

        foreach (var controller in controllers)
        {
            var controllerName = controller.FullName!;
            var exempt = FallbackGatedControllers.ContainsKey(controllerName);

            foreach (var action in GetActionMethods(controller))
            {
                if (HasAnonymous(action, controller))
                {
                    continue; // explicitly anonymous — an intentional, visible decision
                }

                if (HasAuthorizationGate(action, controller))
                {
                    continue; // [Authorize*] or a Require* authorization filter
                }

                if (exempt)
                {
                    continue; // documented fallback-gated surface
                }

                unprotected.Add($"{controllerName}.{action.Name}");
            }
        }

        unprotected.Should().BeEmpty(
            "every controller action must carry [Authorize]/[AllowAnonymous] or a Require* "
            + "authorization filter, or be listed in FallbackGatedControllers with a rationale. "
            + "Unprotected actions found:\n  " + string.Join("\n  ", unprotected.OrderBy(x => x)));
    }

    [Fact]
    public void FallbackGatedControllers_ListIsNotStale()
    {
        // Guard the allow-list itself: every entry must name a real controller that genuinely lacks
        // an attribute gate. If someone later adds [Authorize] to one of these, its entry should be
        // removed so the list stays an honest inventory of fallback-only surfaces.
        foreach (var (typeName, _) in FallbackGatedControllers)
        {
            var type = ApiAssembly.GetType(typeName);
            type.Should().NotBeNull($"{typeName} is listed in FallbackGatedControllers but does not exist");

            var actions = GetActionMethods(type!).ToList();
            actions.Should().NotBeEmpty($"{typeName} should expose at least one action");

            var everyActionGated = actions.All(a => HasAnonymous(a, type!) || HasAuthorizationGate(a, type!));
            everyActionGated.Should().BeFalse(
                $"{typeName} now gates all of its actions explicitly and should be removed from FallbackGatedControllers");
        }
    }

    private static IEnumerable<MethodInfo> GetActionMethods(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName
                        && m.GetCustomAttributes().OfType<IActionHttpMethodProvider>().Any());

    private static bool HasAnonymous(MethodInfo action, Type controller) =>
        action.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any()
        || controller.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any();

    private static bool HasAuthorizationGate(MethodInfo action, Type controller) =>
        HasGate(action.GetCustomAttributes(inherit: true))
        || HasGate(controller.GetCustomAttributes(inherit: true));

    private static bool HasGate(IEnumerable<object> attributes) =>
        attributes.Any(a => a is IAuthorizeData
                            or RequirePermissionAttribute // covers [RequireAdmin]/[RequireRead]/[RequireWrite]
                            or RequireScopeAttribute
                            or RequireInstanceKeyAuthAttribute
                            or RequireAuthenticationAttribute);
}
