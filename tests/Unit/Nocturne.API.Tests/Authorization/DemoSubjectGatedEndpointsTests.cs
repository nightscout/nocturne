using System.Reflection;
using FluentAssertions;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Controllers.V2;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Controllers.V4.Connectors;
using Nocturne.API.Controllers.V4.Identity;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Pins which endpoints refuse the demo tenant's shared visitor account.
/// </summary>
/// <remarks>
/// The premise is in <see cref="DenyDemoSubjectAttribute"/>. Two classes of endpoint must refuse
/// that subject, and neither is protected by a tenant permission check — one because there is no
/// tenant in the request to check against, the other because the demo member legitimately holds
/// the permission involved:
/// <list type="bullet">
/// <item><b>Acting as a platform user</b> — creating a tenant, accepting an invite,
/// requesting membership, minting a subject. Covered by
/// <see cref="DenyDemoSubjectAttributeTests"/> for the filter's own behaviour.</item>
/// <item><b>Accumulating state on the shared subject</b> — an external identity, a chat
/// binding, a guest link. One visitor attaches it, every later visitor reads it (the OIDC
/// identity DTO carries the linking person's <c>Email</c>) and can revoke it. Same leak
/// class as the visitor IPs that <c>DemoTenantService</c> keeps off the session rows.</item>
/// </list>
/// <para>
/// A reflection test rather than one per endpoint: the failure mode being guarded is an
/// attribute silently going missing, which a behavioural test on a different endpoint would
/// not notice. <c>GuestLinkController.ActivateGuestLink</c> is deliberately absent — it is
/// the outside guest redeeming a link, not a member acting.
/// </para>
/// </remarks>
public class DemoSubjectGatedEndpointsTests
{
    public static TheoryData<Type, string> GatedEndpoints => new()
    {
        // Acting as a platform user.
        { typeof(PlatformController), nameof(PlatformController.CreateTenant) },
        { typeof(MyTenantsController), nameof(MyTenantsController.CreateTenant) },
        { typeof(MemberInviteController), nameof(MemberInviteController.AcceptInvite) },
        { typeof(MembershipRequestController), nameof(MembershipRequestController.CreateRequest) },
        { typeof(AuthorizationController), nameof(AuthorizationController.CreateSubject) },

        // Accumulating an external identity on the shared subject.
        { typeof(OidcController), nameof(OidcController.Link) },
        { typeof(OidcController), nameof(OidcController.GetLinkedIdentities) },
        { typeof(OidcController), nameof(OidcController.UnlinkIdentity) },

        // Chat identity bindings. GetLinks lists by tenant, not by subject, so one
        // visitor's Discord or Telegram binding is enumerable by all of them.
        { typeof(ChatIdentityController), nameof(ChatIdentityController.GetLinks) },
        { typeof(ChatIdentityController), nameof(ChatIdentityController.ClaimLink) },
        { typeof(ChatIdentityController), nameof(ChatIdentityController.CreateDirectLink) },
        { typeof(ChatIdentityController), nameof(ChatIdentityController.RevokeLink) },

        // Guest links: credential-minting, and the demo member satisfies the
        // sharing.guest check by way of acting on its own subject, so excluding that
        // atom from the demo role achieves nothing here.
        { typeof(GuestLinkController), nameof(GuestLinkController.CreateGuestLink) },
        { typeof(GuestLinkController), nameof(GuestLinkController.GetGuestLinks) },
        { typeof(GuestLinkController), nameof(GuestLinkController.RevokeGuestLink) },
        { typeof(GuestLinkController), nameof(GuestLinkController.DismissGuestLink) },

        // Connector configuration names the host the server fetches from, and connector status
        // reports what came back — together, aiming a request from inside the deployment's
        // network and reading the result. tenant.settings is in the demo role, so a permission
        // check would pass.
        { typeof(ConfigurationController), nameof(ConfigurationController.SaveConfiguration) },
        { typeof(ConfigurationController), nameof(ConfigurationController.SaveSecrets) },
        { typeof(ConnectorStatusController), nameof(ConnectorStatusController.GetStatus) },

        // Completing the CareLink flow writes the signed-in Medtronic username and country into
        // the tenant's connector configuration, which GET returns in the clear. The service also
        // guards the write, but this flow stores a refresh token as a connector secret on the way
        // and its persist step swallows exceptions, so it has to be refused at the edge too.
        { typeof(CareLinkConnectController), nameof(CareLinkConnectController.Start) },

        // The webhook tester posts to a caller-named destination from inside the deployment's
        // network. It carries no permission attribute at all, so [Authorize] is the whole gate.
        { typeof(WebhookSettingsController), nameof(WebhookSettingsController.TestWebhookSettings) },

        // Session management on a shared subject acts on other visitors' sessions, not the
        // caller's. List stays open — see TheSessionListStaysOpenWhileRevocationDoesNot.
        { typeof(SessionsController), nameof(SessionsController.Revoke) },
        { typeof(SessionsController), nameof(SessionsController.RevokeOthers) },
    };

    [Theory]
    [MemberData(nameof(GatedEndpoints))]
    public void Endpoint_RefusesTheSharedDemoSubject(Type controller, string action)
    {
        var method = controller.GetMethod(action, BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull(
            "{0}.{1} is pinned by this test; renaming it without moving the gate would " +
            "otherwise drop the gate silently",
            controller.Name, action);

        var gated = method!.GetCustomAttribute<DenyDemoSubjectAttribute>() is not null
            || controller.GetCustomAttribute<DenyDemoSubjectAttribute>() is not null;

        gated.Should().BeTrue(
            "{0}.{1} is reachable with a demo session, which any anonymous caller can " +
            "obtain, and it either acts as a platform user or writes state onto the shared " +
            "subject that later visitors can read",
            controller.Name, action);
    }

    /// <summary>
    /// Positive control: proves the assertion discriminates rather than passing because the
    /// attribute lookup always succeeds.
    /// </summary>
    [Fact]
    public void TheGuardDetectsAnUngatedEndpoint()
    {
        var ungated = typeof(GuestLinkController)
            .GetMethod(nameof(GuestLinkController.ActivateGuestLink), BindingFlags.Public | BindingFlags.Instance);

        ungated.Should().NotBeNull();
        ungated!.GetCustomAttribute<DenyDemoSubjectAttribute>().Should().BeNull();
        typeof(GuestLinkController).GetCustomAttribute<DenyDemoSubjectAttribute>().Should().BeNull(
            "the gate is per-endpoint here, so the guest-activation path stays open");
    }

    /// <summary>
    /// Listing sessions stays open while revoking them does not: the rows carry no address — the
    /// repository scrubs them for a demo subject — and no device beyond <c>demo-visitor</c>, so the
    /// list tells a visitor only how many people are on the demo.
    /// </summary>
    [Fact]
    public void TheSessionListStaysOpenWhileRevocationDoesNot()
    {
        var list = typeof(SessionsController)
            .GetMethod(nameof(SessionsController.List), BindingFlags.Public | BindingFlags.Instance);

        list.Should().NotBeNull();
        list!.GetCustomAttribute<DenyDemoSubjectAttribute>().Should().BeNull();
        typeof(SessionsController).GetCustomAttribute<DenyDemoSubjectAttribute>().Should().BeNull(
            "gating the controller would take the list with it");
    }
}
