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
/// (<c>[RequireSuperuser]</c>, <c>[RequireScope]</c>, <c>[RequireInstanceKeyAuth]</c>, …), or an
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
    private static Assembly ApiAssembly => ControllerActionReflection.ApiAssembly;

    /// <summary>
    /// Controllers whose actions are intentionally gated only by the <see cref="HasPermissionsRequirement"/>
    /// fallback policy (and, where they read PHI, per-category public-share RLS or in-handler
    /// <c>HasPermission</c> checks) rather than an authorization attribute. Keyed by full type name
    /// with the rationale as the value.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> FallbackGatedControllers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ── V4 caller-scoped identity read ──────────────────────────────────────────────
            // Answers with the caller's own resolved grant, which discloses nothing a request of
            // theirs would not. The share view needs it to offer only the surfaces it can load, and
            // the share subject is unauthenticated, so [Authorize] would 401 every public share.
            ["Nocturne.API.Controllers.V4.Identity.MyPermissionsController"] = "returns the caller's own grant; fallback rejects an empty trie",

            // The appearance a share or guest renders with, for the anonymous share subject [Authorize]
            // would 401. It answers only a share token or a guest session, and only
            // with presentation fields — no identity, and nothing the viewer's own page does not
            // already display.
            ["Nocturne.API.Controllers.V4.Identity.TenantAppearanceController"] = "presentation-only, share and guest only; fallback rejects an empty trie",

            // ── V3 status and lastModified ──────────────────────────────────────────────────
            // A share host reaches these with its public scopes and no authenticated user, so
            // [Authorize] would 401 every share. The fallback answers anonymous and bad-token
            // callers with the v3 401, and lastModified lists only the collections the caller reads.
            ["Nocturne.API.Controllers.V3.StatusController"] = "v3 credential probe; fallback rejects an empty trie",
            ["Nocturne.API.Controllers.V3.LastModifiedController"] = "scoped to readable collections; fallback rejects an empty trie",

            // ── V4 caller-scoped identity read ──────────────────────────────────────────────
            // Answers with the caller's own resolved grant, which discloses nothing a request of
            // theirs would not. The share view needs it to offer only the reports it can load, and
            // the share subject is unauthenticated, so [Authorize] would 401 every public share.
            ["Nocturne.API.Controllers.V4.Identity.MyPermissionsController"] = "returns the caller's own grant; fallback rejects an empty trie",

            // ── V4 tracker reads ─────────────────────────────────────────────────────────────
            // GET definitions/instances lean on the fallback policy: a bare unauthenticated request
            // on a tenant subdomain has an empty trie and is rejected, so a private tenant exposes
            // no tracker anonymously. A public share or guest link (admitted by the fallback)
            // reads an empty list: the actions refuse it in ReadsNoTrackers, and the share RLS
            // policy hides the tracker tables, which are not in ShareDataCategories. The write
            // actions carry [Authorize] + [RequireDeclaredWriteScope] and are gated explicitly.
            ["Nocturne.API.Controllers.V4.Monitoring.TrackersController"] = "tracker reads gated by fallback trie (rejects bare anonymous); not share-governed",
            // The legacy-age fallbacks read as the tracker reads above, empty for a share or guest
            // link (TrackerReadAccess) and for a caller without devices.read.
            ["Nocturne.API.Controllers.V4.Monitoring.TrackerLegacyFallbacksController"] = "tracker reads gated by fallback trie (rejects bare anonymous); not share-governed",

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

    /// <summary>Why an action outside the versioned planes names no scope.</summary>
    private static class NoScope
    {
        /// <summary>Reads or changes only the caller's own credentials, sessions or sign-in links, keyed by their subject.</summary>
        public const string OwnCredentials = "the caller's own credentials or sessions";

        /// <summary>Hands part of the caller's own grant to a client or token, refused beyond the scopes the caller holds.</summary>
        public const string OwnDelegation = "delegates the caller's own grant, no wider than it";

        /// <summary>The row is found by a code the caller presents, so holding the code is the control.</summary>
        public const string CapabilityCode = "row found by a code the caller presents";

        /// <summary>Answers only for a token issued to the caller; any other reads as inactive.</summary>
        public const string OwnTokens = "answers only for the caller's own tokens";

        /// <summary>
        /// Writes tenant state that no scope governs yet. Recorded so the decision is visible rather
        /// than answered here.
        /// </summary>
        public const string Undecided = "tenant state, governing scope undecided";
    }

    /// <summary>
    /// Actions outside the V1 to V4 planes that name no scope, keyed <c>Controller.Action</c>. Every
    /// other action there declares its admission (<see cref="ControllerActionReflection.DeclaresAdmission"/>).
    /// The versioned planes hold the same rule with their own lists:
    /// <see cref="ReadEndpointScopeEnforcementTests"/>, <see cref="WriteEndpointScopeEnforcementTests"/>
    /// and <see cref="V4WriteScopeGatingTests"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> UnversionedScopeExemptActions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DirectGrantController.Create"] = NoScope.OwnDelegation,
            ["OAuthController.ApproveConsent"] = NoScope.OwnDelegation,
            ["OAuthController.DeviceApprove"] = NoScope.OwnDelegation,
            ["OAuthController.UpdateGrant"] = NoScope.OwnDelegation,
            ["OAuthController.GetGrants"] = NoScope.OwnCredentials,
            ["OAuthController.DeleteGrant"] = NoScope.OwnCredentials,
            ["OAuthController.GetDeviceInfo"] = NoScope.CapabilityCode,
            ["OAuthController.Introspect"] = NoScope.OwnTokens,
            ["OidcController.Link"] = NoScope.OwnCredentials,
            ["OidcController.LinkCallback"] = NoScope.OwnCredentials,
            ["OidcController.GetLinkedIdentities"] = NoScope.OwnCredentials,
            ["OidcController.UnlinkIdentity"] = NoScope.OwnCredentials,
            ["OidcController.Logout"] = NoScope.OwnCredentials,
            ["OidcController.GetUserInfo"] = NoScope.OwnCredentials,
            ["PasskeyController.ListCredentials"] = NoScope.OwnCredentials,
            ["PasskeyController.RemoveCredential"] = NoScope.OwnCredentials,
            ["PasskeyController.RegenerateRecoveryCodes"] = NoScope.OwnCredentials,
            ["PasskeyController.GetRecoveryStatus"] = NoScope.OwnCredentials,
            ["PasskeyController.CompleteOnboarding"] = NoScope.Undecided,
            ["TotpController.Setup"] = NoScope.OwnCredentials,
            ["TotpController.VerifySetup"] = NoScope.OwnCredentials,
            ["TotpController.ListCredentials"] = NoScope.OwnCredentials,
            ["TotpController.RemoveCredential"] = NoScope.OwnCredentials,
            ["TrustedDevicesController.List"] = NoScope.OwnCredentials,
            ["TrustedDevicesController.Revoke"] = NoScope.OwnCredentials,
            ["TrustedDevicesController.RevokeAll"] = NoScope.OwnCredentials,
        };

    [Fact]
    public void EveryUnversionedAction_DeclaresItsAdmissionOrIsListed()
    {
        var actions = UnversionedActions().ToList();

        actions.Should().HaveCountGreaterThan(50,
            "the scan should discover the authentication and discovery endpoints");

        var undeclared = actions
            .Where(a => !ControllerActionReflection.DeclaresAdmission(a.Action, a.Controller)
                        && !UnversionedScopeExemptActions.ContainsKey(Key(a)))
            .Select(Key)
            .ToList();

        undeclared.Should().BeEmpty(
            "every action outside the V1 to V4 planes must declare its scope, the guard that decides "
            + "it, or a credential that is not a scope, or be listed in UnversionedScopeExemptActions "
            + "with the reason it needs none. Undeclared: " + string.Join("; ", undeclared));
    }

    [Fact]
    public void UnversionedScopeExemptActions_NameAnUndeclaredAction()
    {
        var undeclared = UnversionedActions()
            .Where(a => !ControllerActionReflection.DeclaresAdmission(a.Action, a.Controller))
            .Select(Key)
            .ToHashSet(StringComparer.Ordinal);

        UnversionedScopeExemptActions.Keys.Should().BeSubsetOf(undeclared,
            "an entry whose action is gone or now declares its admission would excuse a later action "
            + "that reuses the name");
    }

    [Fact]
    public void EveryScopeDecidedByMember_Exists()
    {
        var markers = ControllerActionReflection.GetControllers()
            .SelectMany(c => GetActionMethods(c).Select(a => (Controller: c, Action: a)))
            .SelectMany(x => x.Action.GetCustomAttributes<ScopeDecidedByAttribute>()
                .Select(d => (Key: Key(x), Decided: d)))
            .ToList();

        markers.Should().Contain(m => m.Decided.Member != null,
            "the scan must find a marker that names a member, or this test passes vacuously");

        var missing = markers
            .Where(m => m.Decided.Member is { } member
                        && m.Decided.Type.GetMember(
                            member,
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                            .Length == 0)
            .Select(m => $"{m.Key} names {m.Decided.Type.Name}.{m.Decided.Member}")
            .ToList();

        missing.Should().BeEmpty("a [ScopeDecidedBy] must name a member its type declares: "
            + string.Join("; ", missing));
    }

    /// <summary>
    /// A guard's call in a handler is invisible to every attribute sweep, so each type that can
    /// refuse a caller for a missing scope must be named by at least one
    /// <see cref="ScopeDecidedByAttribute"/>. A type refuses when it exposes
    /// <c>FindMissingScope</c>, a <c>Required*Scope</c> naming the scope a refusal reports, or a
    /// static <c>Refuse*</c> method that returns an action result.
    /// </summary>
    /// <remarks>
    /// It cannot see a guard with one marked caller and one unmarked caller, which only reading the
    /// handlers would find.
    /// </remarks>
    [Fact]
    public void EveryRefusingScopeGuard_IsNamedByAScopeDecidedBy()
    {
        var refusing = ControllerActionReflection.ApiAssembly.GetTypes()
            .Where(t => !typeof(ControllerBase).IsAssignableFrom(t) && RefusesForAMissingScope(t))
            .ToList();

        refusing.Select(t => t.Name).Should().Contain(
            ["StateSpanWriteScopeGuard", "ActivityWriteScopeGuard", "StateSpanReadScopeGuard"],
            "the scan must find the known refusing guards, or this test passes vacuously");

        var named = ControllerActionReflection.GetControllers()
            .SelectMany(c => GetActionMethods(c))
            .SelectMany(a => a.GetCustomAttributes<ScopeDecidedByAttribute>())
            .Select(d => d.Type)
            .ToHashSet();

        var unnamed = refusing.Where(t => !named.Contains(t)).Select(t => t.FullName).ToList();

        unnamed.Should().BeEmpty(
            "every type that refuses for a missing scope must be named by a [ScopeDecidedBy] on the "
            + "actions that call it. Unnamed: " + string.Join("; ", unnamed));
    }

    private static bool RefusesForAMissingScope(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Any(m => m.Name == "FindMissingScope"
                      || (m.Name.StartsWith("Required", StringComparison.Ordinal)
                          && m.Name.EndsWith("Scope", StringComparison.Ordinal)
                          && m.ReturnType == typeof(string))
                      || (m.Name.StartsWith("Refuse", StringComparison.Ordinal)
                          && typeof(IActionResult).IsAssignableFrom(m.ReturnType)));

    private static IEnumerable<(Type Controller, MethodInfo Action)> UnversionedActions() =>
        ControllerActionReflection.GetControllers()
            .Where(c => !IsVersioned(c))
            .SelectMany(c => GetActionMethods(c).Select(a => (c, a)));

    private static bool IsVersioned(Type controller) =>
        controller.Namespace is { } ns
        && new[] { "V1", "V2", "V3", "V4" }.Any(v =>
            ns == $"Nocturne.API.Controllers.{v}"
            || ns.StartsWith($"Nocturne.API.Controllers.{v}.", StringComparison.Ordinal));

    private static string Key((Type Controller, MethodInfo Action) a) => $"{a.Controller.Name}.{a.Action.Name}";

    [Fact]
    public void EveryControllerAction_HasAnExplicitAuthorizationDecision()
    {
        var controllers = ControllerActionReflection.GetControllers().ToList();

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
        ControllerActionReflection.GetActionMethods(controller);

    private static bool HasAnonymous(MethodInfo action, Type controller) =>
        ControllerActionReflection.HasAnonymous(action, controller);

    private static bool HasAuthorizationGate(MethodInfo action, Type controller) =>
        ControllerActionReflection.HasAuthorizationGate(action, controller);
}
