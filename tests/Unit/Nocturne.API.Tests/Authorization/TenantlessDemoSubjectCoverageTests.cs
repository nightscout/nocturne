using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Discovers the endpoints an anonymous visitor holding a demo session can reach off the demo
/// tenant, and requires each to be gated with <see cref="DenyDemoSubjectAttribute"/> or named in
/// <see cref="Exempt"/>.
/// </summary>
/// <remarks>
/// <para>
/// The surface is the paths <see cref="TenantResolutionMiddleware.IsTenantlessAllowed"/> admits
/// with no resolved tenant, intersected with the endpoints whose only gate is <em>bare</em>
/// <c>[Authorize]</c> — not "endpoints with no attribute". An attribute-less endpoint falls to the
/// fallback policy (<see cref="HasPermissionsRequirement"/>), which the demo subject fails off a
/// tenant: its permissions come from a tenant membership while the fallback reads only global
/// <c>subject_roles</c>, which is empty for it. A bare <c>[Authorize]</c> drops that to the
/// framework's "is authenticated", which every demo session is. Widening the predicate would
/// pull in endpoints the demo subject cannot reach anyway.
/// </para>
/// <para>
/// An endpoint gated by roles, a policy, or a Nocturne <c>[Require*]</c> filter asks for more than
/// authentication; one carrying <c>[AllowAnonymous]</c> was never gated by a session at all. This
/// discovers, so anything added to the surface later must be classified before it ships —
/// <see cref="DemoSubjectGatedEndpointsTests"/> pins what is already decided and cannot see a new
/// endpoint.
/// </para>
/// </remarks>
public class TenantlessDemoSubjectCoverageTests
{
    /// <summary>
    /// Endpoints on this surface deliberately left reachable by a demo visitor, keyed by
    /// <c>Controller.Action</c> with the reason.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Exempt =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // A demo subject holds the demo tenant's membership and nothing else — the reset
            // deletes any it picked up elsewhere — so both return the tenant already on screen.
            ["PlatformController.GetTenants"] = "lists the caller's own memberships, which for the demo subject is the demo tenant",
            ["MyTenantsController.GetOverview"] = "aggregates over the caller's own memberships, which for the demo subject is the demo tenant",

            ["PlatformController.GetTransitionStatus"] = "reports the deployment's multitenancy configuration, not subject state",
        };

    [Fact]
    public void EveryTenantlessAuthenticationOnlyEndpoint_RefusesTheDemoSubjectOrIsExempt()
    {
        var ungated = Discover()
            .Where(e => !ControllerActionReflection.DeniesTheDemoSubject(e.Action, e.Controller))
            .Select(e => Key(e.Controller, e.Action))
            .Where(k => !Exempt.ContainsKey(k))
            .Distinct()
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        ungated.Should().BeEmpty(
            "an anonymous caller can obtain a demo session, and off a tenant nothing but "
            + "authentication stands in front of these — gate them with [DenyDemoSubject] or add "
            + "them to Exempt with a reason. Ungated:\n  " + string.Join("\n  ", ungated));
    }

    /// <summary>
    /// Non-vacuity: a discovery that enumerates nothing passes the guard above while checking
    /// nothing, so pin that the surface is found and that a known member of it is on it.
    /// </summary>
    [Fact]
    public void TheDiscoveryFindsTheTenantlessSurface()
    {
        var discovered = Discover().Select(e => Key(e.Controller, e.Action)).Distinct().ToList();

        discovered.Should().NotBeEmpty(
            "the tenantless surface has authentication-only endpoints on it, so a discovery that "
            + "returns nothing has stopped matching routes rather than found the surface clean");

        discovered.Should().Contain(
            $"{nameof(PlatformController)}.{nameof(PlatformController.CreateTenant)}",
            "tenant creation is served off the apex under a bare [Authorize] and is the endpoint "
            + "this surface was gated for");
    }

    /// <summary>
    /// Non-vacuity on the gate check, and staleness on the list: an exemption must name an endpoint
    /// that is still discovered and still ungated, so the guard above cannot be passing because
    /// everything reads as gated.
    /// </summary>
    [Fact]
    public void EveryExemptionNamesADiscoveredUngatedEndpoint()
    {
        var discovered = Discover().ToDictionary(e => Key(e.Controller, e.Action), e => e);

        foreach (var (key, reason) in Exempt)
        {
            discovered.Should().ContainKey(key,
                "{0} is exempted with the reason \"{1}\" but is no longer on the tenantless "
                + "authentication-only surface — drop the entry", key, reason);

            var endpoint = discovered[key];
            ControllerActionReflection.DeniesTheDemoSubject(endpoint.Action, endpoint.Controller)
                .Should().BeFalse(
                    "{0} now carries [DenyDemoSubject], so its exemption is dead — drop the entry",
                    key);
        }
    }

    private static string Key(Type controller, MethodInfo action) => $"{controller.Name}.{action.Name}";

    private static IEnumerable<(Type Controller, MethodInfo Action)> Discover()
    {
        foreach (var controller in ControllerActionReflection.GetControllers())
        {
            foreach (var action in ControllerActionReflection.GetActionMethods(controller))
            {
                if (ControllerActionReflection.HasAnonymous(action, controller))
                    continue;

                if (!AuthenticationIsTheOnlyGate(action, controller))
                    continue;

                if (!ControllerActionReflection.GetRoutes(controller, action)
                        .Any(TenantResolutionMiddleware.IsTenantlessAllowed))
                    continue;

                yield return (controller, action);
            }
        }
    }

    private static bool AuthenticationIsTheOnlyGate(MethodInfo action, Type controller)
    {
        var attributes = action.GetCustomAttributes(inherit: true)
            .Concat(controller.GetCustomAttributes(inherit: true))
            .ToList();

        // No demo grant satisfies one of these off a tenant.
        if (attributes.Any(a => a is RequirePermissionAttribute
                                or RequireScopeAttribute
                                or RequireInstanceKeyAuthAttribute))
        {
            return false;
        }

        var authorize = attributes.OfType<IAuthorizeData>().ToList();

        // A named role or policy asks for something the demo subject, which holds no global role,
        // does not have.
        if (authorize.Any(a => !string.IsNullOrEmpty(a.Roles) || !string.IsNullOrEmpty(a.Policy)))
        {
            return false;
        }

        return authorize.Count > 0 || attributes.Any(a => a is RequireAuthenticationAttribute);
    }
}
