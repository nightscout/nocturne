using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Discovers the endpoints an anonymous visitor holding a demo session can reach off the demo
/// tenant, and requires each to be gated with <see cref="DenyDemoSubjectAttribute"/> or named in
/// its bucket's exemption list with a reason.
/// </summary>
/// <remarks>
/// <para>
/// The surface is the paths <see cref="TenantResolutionMiddleware.IsTenantlessAllowed"/> admits
/// with no resolved tenant, in three buckets.
/// </para>
/// <para>
/// <see cref="AuthenticationOnly"/> is the endpoints whose only gate is <em>bare</em>
/// <c>[Authorize]</c>, which drops authorization to the framework's "is authenticated" — and every
/// demo session is.
/// </para>
/// <para>
/// <see cref="Anonymous"/> is the endpoints carrying <c>[AllowAnonymous]</c>. That attribute says
/// several kinds of caller reach the endpoint, not that no caller is identified — a body reading
/// <c>GetAuthContext()</c> resolves a demo session like any other, and such a call is invisible to
/// metadata. So the whole slice is classified by hand rather than narrowed by reflection: a
/// heuristic that missed one would read as protection while providing none.
/// </para>
/// <para>
/// <see cref="FallbackPolicy"/> is the endpoints carrying no authorization attribute at all. They
/// fall to <see cref="HasPermissionsRequirement"/>, which the demo subject fails off a tenant
/// because its permissions come from a tenant membership while the fallback reads only global
/// <c>subject_roles</c>, empty for it. That refusal is real but it lives two layers away and holds
/// for a reason about the demo subject's provenance rather than about the endpoint, so these are
/// classified here too — most already carry the gate, and the sign-in-factor endpoints among them
/// are the ones that would matter if the fallback ever loosened.
/// </para>
/// <para>
/// What this cannot see is an endpoint gated by roles, a policy, or a Nocturne <c>[Require*]</c>
/// filter: those ask for more than authentication, and the demo subject holds no global role or
/// scope off a tenant, so it fails them on the endpoint's own terms. That is the one deliberate
/// exclusion.
/// </para>
/// <para>
/// This discovers, so an endpoint joining any bucket later must be classified before it ships —
/// <see cref="DemoSubjectGatedEndpointsTests"/> pins what is already decided and cannot see a new
/// endpoint.
/// </para>
/// </remarks>
public class TenantlessDemoSubjectCoverageTests
{
    /// <summary>
    /// Endpoints on the bare-<c>[Authorize]</c> surface deliberately left reachable by a demo
    /// visitor, keyed by <c>Controller.Action</c> with the reason.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AuthenticationOnlyExempt =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // A demo subject holds the demo tenant's membership and nothing else — the reset
            // deletes any it picked up elsewhere — so both return the tenant already on screen.
            ["PlatformController.GetTenants"] = "lists the caller's own memberships, which for the demo subject is the demo tenant",
            ["MyTenantsController.GetOverview"] = "aggregates over the caller's own memberships, which for the demo subject is the demo tenant",
            ["MyTenantsController.GetMyTenants"] = "lists the caller's own memberships, which for the demo subject is the demo tenant",
            ["MyPermissionsController.GetMyPermissions"] = "returns the caller's own global subject-role scopes; tenant-derived scopes are not applied off a tenant",

            ["PlatformController.GetTransitionStatus"] = "reports the deployment's multitenancy configuration, not subject state",

            ["UserPreferencesController.GetPreferences"] = "returns the caller's own units and formats, which for the demo subject are the demo account's public display defaults",
        };

    private const string DevOnly =
        "lives in the .DevOnly namespace, which DevOnlyExcludingControllerFeatureProvider drops "
        + "from controller discovery outside Development, so it is on no deployed surface";

    private const string FirstRunOnly =
        "refuses once the instance has an owner, and an instance serving a demo tenant has one";

    /// <summary>
    /// Endpoints on the <c>[AllowAnonymous]</c> surface deliberately left reachable by a demo
    /// visitor, keyed by <c>Controller.Action</c> with the reason.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AnonymousExempt =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AvatarController.Get"] = "serves any subject's picture by id to anyone; with no id it falls back to the caller's own subject, which for a demo session is the demo account's own picture",

            ["DevAdminController.CreateTenant"] = DevOnly,
            ["DevAdminController.DeleteTenant"] = DevOnly,
            ["DevAdminController.EnterRecoveryMode"] = DevOnly,
            ["DevAdminController.ExportSnapshot"] = DevOnly,
            ["DevAdminController.ImportScopedSnapshot"] = DevOnly,
            ["DevAdminController.ImportSnapshot"] = DevOnly,
            ["DevAdminController.ListTenants"] = DevOnly,
            ["DevAdminController.SeedSampleData"] = DevOnly,
            ["DevAdminController.SeedTenant"] = DevOnly,
            ["DevAdminController.SyncAll"] = DevOnly,
            ["DevAuthController.ExportPasskeyFixture"] = DevOnly,
            ["DevAuthController.Login"] = DevOnly,
            ["DevAuthController.LoginJson"] = DevOnly,

            ["OidcController.GetProviders"] = "lists the deployment's enabled identity providers so the sign-in page can render its buttons; reads no session",
            ["OidcController.Login"] = "starts the identity-provider sign-in and redirects to the provider; reads no session",
            ["OidcController.Callback"] = "mints the session; the subject comes from the provider's token response, not from any session on the request",
            ["OidcController.Refresh"] = "mints the session; the subject comes from the refresh token presented, not from any session on the request",
            ["OidcController.GetSession"] = "reports the caller's own session back to the caller, which for a demo session is the demo account the demo UI is already signed in as",

            ["PlatformAccessController.Access"] = "resolves the caller's subject but mints the grant only for one holding the global subjects.is_platform_admin flag, which no demo subject does; the refusal is audited",

            ["SetupController.CreateTenant"] = FirstRunOnly,
            ["SetupController.OwnerOptions"] = FirstRunOnly,
            ["SetupController.OwnerComplete"] = FirstRunOnly,
            ["SetupController.OwnerOidc"] = FirstRunOnly,
            ["SetupController.OidcCallback"] = FirstRunOnly,

            ["StatusController.GetStatus"] = "reports the deployment's version, settings and setup state to any caller, signed in or not; reads no session",
            ["TlsAuthorizationController.Authorize"] = "answers the edge's on-demand-TLS ask for a hostname; it takes no credential and reads no session",
            ["TotpController.Login"] = "mints the session; the subject comes from the passkey step-up token, and it additionally requires membership of the resolved tenant, which a tenantless host has none of",
        };

    /// <summary>
    /// Endpoints on the fallback-policy surface deliberately left reachable by a demo visitor,
    /// keyed by <c>Controller.Action</c> with the reason.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> FallbackPolicyExempt =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OidcController.Logout"] = "revokes the refresh token the caller presented and clears the caller's own cookies; it reads the subject only to attribute the audit row, so a demo visitor signing themselves out reaches nothing another visitor holds",
        };

    [Fact]
    public void EveryTenantlessAuthenticationOnlyEndpoint_RefusesTheDemoSubjectOrIsExempt() =>
        AssertEveryEndpointIsGatedOrExempt(AuthenticationOnly);

    [Fact]
    public void EveryTenantlessAnonymousEndpoint_RefusesTheDemoSubjectOrIsExempt() =>
        AssertEveryEndpointIsGatedOrExempt(Anonymous);

    [Fact]
    public void TheAuthenticationOnlyDiscoveryFindsItsSurface() =>
        AssertTheDiscoveryFindsTheSurface(AuthenticationOnly);

    [Fact]
    public void TheAnonymousDiscoveryFindsItsSurface() =>
        AssertTheDiscoveryFindsTheSurface(Anonymous);

    [Fact]
    public void EveryAuthenticationOnlyExemptionNamesADiscoveredUngatedEndpoint() =>
        AssertEveryExemptionIsLive(AuthenticationOnly);

    [Fact]
    public void EveryAnonymousExemptionNamesADiscoveredUngatedEndpoint() =>
        AssertEveryExemptionIsLive(Anonymous);

    [Fact]
    public void EveryTenantlessFallbackPolicyEndpoint_RefusesTheDemoSubjectOrIsExempt() =>
        AssertEveryEndpointIsGatedOrExempt(FallbackPolicy);

    [Fact]
    public void TheFallbackPolicyDiscoveryFindsItsSurface() =>
        AssertTheDiscoveryFindsTheSurface(FallbackPolicy);

    [Fact]
    public void EveryFallbackPolicyExemptionNamesADiscoveredUngatedEndpoint() =>
        AssertEveryExemptionIsLive(FallbackPolicy);

    private static void AssertEveryEndpointIsGatedOrExempt(Surface surface)
    {
        var ungated = surface.Discover()
            .Where(e => !ControllerActionReflection.DeniesTheDemoSubject(e.Action, e.Controller))
            .Select(e => Key(e.Controller, e.Action))
            .Where(k => !surface.Exempt.ContainsKey(k))
            .Distinct()
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        ungated.Should().BeEmpty(
            "an anonymous caller can obtain a demo session, and off a tenant " + surface.Reachability
            + " — gate them with [DenyDemoSubject] or add them to the " + surface.Name
            + " exemptions with a reason. Ungated:\n  " + string.Join("\n  ", ungated));
    }

    /// <summary>
    /// Non-vacuity: a discovery that enumerates nothing passes the guard above while checking
    /// nothing, so pin that the surface is found and that a known member of it is on it.
    /// </summary>
    private static void AssertTheDiscoveryFindsTheSurface(Surface surface)
    {
        var discovered = surface.Discover().Select(e => Key(e.Controller, e.Action)).Distinct().ToList();

        discovered.Should().NotBeEmpty(
            "the tenantless {0} surface has endpoints on it, so a discovery that returns nothing "
            + "has stopped matching routes rather than found the surface clean", surface.Name);

        discovered.Should().Contain(surface.Witness, surface.WitnessReason);
    }

    /// <summary>
    /// Non-vacuity on the gate check, and staleness on the list: an exemption must name an endpoint
    /// that is still discovered and still ungated, so the guard above cannot be passing because
    /// everything reads as gated.
    /// </summary>
    private static void AssertEveryExemptionIsLive(Surface surface)
    {
        var ungatedByKey = surface.Discover()
            .GroupBy(e => Key(e.Controller, e.Action), StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Any(e => !ControllerActionReflection.DeniesTheDemoSubject(e.Action, e.Controller)),
                StringComparer.Ordinal);

        foreach (var (key, reason) in surface.Exempt)
        {
            ungatedByKey.Should().ContainKey(key,
                "{0} is exempted with the reason \"{1}\" but is no longer on the tenantless {2} "
                + "surface — drop the entry", key, reason, surface.Name);

            ungatedByKey[key].Should().BeTrue(
                "{0} now carries [DenyDemoSubject], so its exemption is dead — drop the entry", key);
        }
    }

    private static string Key(Type controller, MethodInfo action) => $"{controller.Name}.{action.Name}";

    /// <summary>One bucket of the tenantless surface, and what a failure on it should say.</summary>
    /// <param name="Name">Names the bucket in failure messages.</param>
    /// <param name="OnTheSurface">Whether an action belongs to this bucket, before route matching.</param>
    /// <param name="Reachability">What stands between an anonymous caller and these endpoints.</param>
    /// <param name="Witness">An endpoint whose absence means discovery has stopped working.</param>
    private sealed record Surface(
        string Name,
        Func<MethodInfo, Type, bool> OnTheSurface,
        IReadOnlyDictionary<string, string> Exempt,
        string Reachability,
        string Witness,
        string WitnessReason)
    {
        public IEnumerable<(Type Controller, MethodInfo Action)> Discover()
        {
            foreach (var controller in ControllerActionReflection.GetControllers())
            {
                foreach (var action in ControllerActionReflection.GetActionMethods(controller))
                {
                    if (!OnTheSurface(action, controller))
                        continue;

                    // No method: this sweeps the whole tenantless surface, so a path reachable
                    // under any method has to be covered.
                    if (!ControllerActionReflection.GetRoutes(controller, action)
                            .Any(route => TenantResolutionMiddleware.IsTenantlessAllowed(route)))
                        continue;

                    yield return (controller, action);
                }
            }
        }
    }

    private static readonly Surface AuthenticationOnly = new(
        "authentication-only",
        (action, controller) => !ControllerActionReflection.HasAnonymous(action, controller)
                                && AuthenticationIsTheOnlyGate(action, controller),
        AuthenticationOnlyExempt,
        "nothing but authentication stands in front of these",
        $"{nameof(PlatformController)}.{nameof(PlatformController.CreateTenant)}",
        "tenant creation is served off the apex under a bare [Authorize] and is the endpoint this "
        + "surface was gated for");

    private static readonly Surface Anonymous = new(
        "[AllowAnonymous]",
        ControllerActionReflection.HasAnonymous,
        AnonymousExempt,
        "these are reachable with no session at all, and one that resolves a subject from the "
        + "session anyway resolves a demo session",
        $"{nameof(PasskeyController)}.{nameof(PasskeyController.RegisterOptions)}",
        "passkey enrolment is [AllowAnonymous] for the recovery flow yet binds an authenticator to "
        + "whichever subject the caller's session resolves to, which is the shape this bucket "
        + "exists to catch");

    private static readonly Surface FallbackPolicy = new(
        "fallback-policy",
        (action, controller) => !ControllerActionReflection.HasAnonymous(action, controller)
                                && !ControllerActionReflection.HasAuthorizationGate(action, controller),
        FallbackPolicyExempt,
        "nothing on the endpoint itself stands in front of these — they are held off the demo "
        + "subject by the fallback policy alone, which refuses it only because its permissions "
        + "come from a tenant membership and no tenant is resolved here",
        $"{nameof(PasskeyController)}.{nameof(PasskeyController.ListCredentials)}",
        "the caller's own sign-in factors are served off the apex with no authorization attribute "
        + "at all, and are the largest part of this surface");

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
