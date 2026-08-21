using System.Net;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Extensions;
using Nocturne.API.Tests.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.RateLimiting;

/// <summary>
/// Guards the bounds on the anonymous surfaces outside the passkey ceremonies: first-run setup, the
/// availability probes, and the invite lookups a caller reaches with a token alone. Sweeps rather
/// than lists, so an action added to any of these controllers has to answer the same question.
/// </summary>
/// <seealso cref="Nocturne.API.Tests.Controllers.PasskeyRateLimitTests"/>
public class AnonymousSurfaceRateLimitTests
{
    /// <summary>
    /// The controllers whose anonymous actions are swept, beyond the wholly anonymous
    /// <see cref="SetupController"/>.
    /// </summary>
    private static readonly Type[] PartlyAnonymousControllers =
    [
        typeof(MemberInviteController),
        typeof(AlertInvitesController),
        typeof(MyTenantsController),
    ];

    /// <summary>
    /// The whole controller is <c>[AllowAnonymous]</c> and reaches the database before any
    /// credential exists on the instance, so every action on it carries a ceiling.
    /// </summary>
    [Fact]
    public void EverySetupAction_CarriesAClientAddressPolicy()
    {
        var actions = Actions(typeof(SetupController));

        // A sweep that discovers nothing would pass while guarding nothing.
        actions.Should().HaveCountGreaterThan(4, "the scan should discover the setup actions");

        Uncovered(actions).Should().BeEmpty();
    }

    /// <summary>
    /// Each of these answers from the database on a caller-supplied string with no session, so the
    /// anonymous request has to cost a permit.
    /// </summary>
    [Fact]
    public void EveryAnonymousLookup_CarriesAClientAddressPolicy()
    {
        var actions = PartlyAnonymousControllers
            .SelectMany(Actions)
            .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .ToList();

        actions.Should().HaveCount(3, "the scan should discover the anonymous lookups");

        Uncovered(actions).Should().BeEmpty();
    }

    /// <summary>
    /// Each ceiling, pinned so widening one is a deliberate edit rather than a side effect of
    /// renaming a policy.
    /// </summary>
    [Theory]
    [InlineData(typeof(SetupController), nameof(SetupController.CreateTenant), "setup")]
    [InlineData(typeof(SetupController), nameof(SetupController.OwnerOptions), "setup")]
    [InlineData(typeof(SetupController), nameof(SetupController.OwnerComplete), "setup")]
    [InlineData(typeof(SetupController), nameof(SetupController.OwnerOidc), "setup")]
    [InlineData(typeof(SetupController), nameof(SetupController.OidcCallback), "setup")]
    [InlineData(typeof(SetupController), nameof(SetupController.ValidateUsername), "name-availability")]
    [InlineData(typeof(MyTenantsController), nameof(MyTenantsController.ValidateSlug), "name-availability")]
    [InlineData(typeof(MemberInviteController), nameof(MemberInviteController.GetInviteInfo), "invite-lookup")]
    [InlineData(typeof(AlertInvitesController), nameof(AlertInvitesController.ValidateInvite), "invite-lookup")]
    public void EachAnonymousAction_CarriesItsOwnPolicy(Type controller, string action, string policy)
    {
        PolicyOn(Action(controller, action)).Should().Be(policy);
    }

    /// <summary>
    /// The availability probes have to survive a form that asks per keystroke, so their ceiling is
    /// the loosest of the three — pinned against being tightened to a ceremony's number.
    /// </summary>
    [Fact]
    public void TheAvailabilityProbes_AreLooserThanTheSetupCeremonies()
    {
        PermitLimit("name-availability").Should().BeGreaterThan(PermitLimit("setup"));
    }

    /// <summary>
    /// The ceiling over the real pipeline: the limiter runs ahead of the setup gate, so a caller
    /// probing usernames is turned away before the membership query and before the operator's
    /// validation webhook is called.
    /// </summary>
    /// <remarks>
    /// On its own host and partitioning on the connection, for the reason given on
    /// <see cref="Nocturne.API.Tests.Controllers.PasskeyRateLimitTests"/>'s behavioural test.
    /// </remarks>
    [Fact]
    public async Task ValidateUsername_TurnsAwayTheRequestPastTheCeiling()
    {
        await AssertCeilingHolds("name-availability", "/api/v4/setup/validate-username?username=taken");
    }

    [Fact]
    public async Task ValidateInvite_TurnsAwayTheRequestPastTheCeiling()
    {
        await AssertCeilingHolds("invite-lookup", "/api/v4/alert-invites/no-such-token");
    }

    private static async Task AssertCeilingHolds(string policy, string url)
    {
        var permitLimit = PermitLimit(policy);

        await using var factory = new AuthenticationTestFactory();
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= permitLimit; attempt++)
        {
            var allowed = await client.GetAsync(url);
            allowed.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
                $"attempt {attempt} is within the ceiling of {permitLimit}");
        }

        var rejected = await client.GetAsync(url);

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("rate_limit_exceeded",
            "a throttled caller reads the same body here as on every other limited endpoint");
    }

    private static int PermitLimit(string policy) =>
        ServiceRegistrationExtensions.ClientAddressPolicies.Single(p => p.Policy == policy).PermitLimit;

    private static List<string> Uncovered(IEnumerable<MethodInfo> actions)
    {
        var table = ServiceRegistrationExtensions.ClientAddressPolicies
            .Select(p => p.Policy)
            .ToHashSet();

        return actions
            .Where(a => PolicyOn(a) is not { } policy || !table.Contains(policy))
            .Select(a => $"{a.DeclaringType!.Name}.{a.Name}")
            .ToList();
    }

    private static string? PolicyOn(MethodInfo action) =>
        action.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName;

    private static List<MethodInfo> Actions(Type controller) =>
        controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToList();

    private static MethodInfo Action(Type controller, string name) =>
        controller.GetMethod(name)
        ?? throw new InvalidOperationException($"No action named {name} on {controller.Name}.");
}
