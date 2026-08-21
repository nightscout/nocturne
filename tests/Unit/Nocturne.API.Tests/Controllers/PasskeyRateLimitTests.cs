using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Extensions;
using Nocturne.API.Tests.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Guards the bounds on the passkey ceremonies. Every one of them is reachable without a session,
/// and one — recovery-code verification — takes a secret a person types, so the sweep pins the rule
/// that an anonymous ceremony carries a ceiling rather than today's endpoint list.
/// </summary>
public class PasskeyRateLimitTests
{
    [Fact]
    public void EveryAnonymousPasskeyCeremony_CarriesAClientAddressPolicy()
    {
        var ceremonies = AnonymousCeremonies();

        // A sweep that discovers nothing would pass while guarding nothing.
        ceremonies.Should().HaveCountGreaterThan(10,
            "the scan should discover the anonymous passkey ceremonies");

        var uncovered = ceremonies
            .Where(a => PolicyOn(a) is null)
            .Select(a => a.Name)
            .ToList();

        uncovered.Should().BeEmpty(
            "an anonymous ceremony with no ceiling can be replayed without bound. Uncovered: "
            + string.Join(", ", uncovered));

        var policyNames = ServiceRegistrationExtensions.ClientAddressPolicies
            .Select(p => p.Policy)
            .ToList();

        ceremonies.Select(PolicyOn).Distinct().Should().BeSubsetOf(policyNames,
            "a policy outside the table keys on the connection, which for a remote function is "
            + "the SSR container shared by every user");
    }

    /// <summary>
    /// Each ceremony's ceiling, pinned so widening one is a deliberate edit rather than a side
    /// effect of renaming a policy.
    /// </summary>
    [Theory]
    [InlineData(nameof(PasskeyController.LoginOptions), "passkey-login")]
    [InlineData(nameof(PasskeyController.DiscoverableLoginOptions), "passkey-login")]
    [InlineData(nameof(PasskeyController.LoginComplete), "passkey-login")]
    [InlineData(nameof(PasskeyController.RegisterOptions), "passkey-register")]
    [InlineData(nameof(PasskeyController.RegisterComplete), "passkey-register")]
    [InlineData(nameof(PasskeyController.RecoveryModeOptions), "passkey-register")]
    [InlineData(nameof(PasskeyController.RecoveryModeComplete), "passkey-register")]
    [InlineData(nameof(PasskeyController.InviteOptions), "passkey-register")]
    [InlineData(nameof(PasskeyController.InviteComplete), "passkey-register")]
    [InlineData(nameof(PasskeyController.RecoveryVerify), "passkey-recovery")]
    [InlineData(nameof(PasskeyController.AccessRequestOptions), "passkey-access-request")]
    [InlineData(nameof(PasskeyController.AccessRequestComplete), "passkey-access-request")]
    public void EachCeremony_CarriesItsOwnPolicy(string action, string policy)
    {
        PolicyOn(Action(action)).Should().Be(policy);
    }

    /// <summary>
    /// The management actions are reached with a session, which is its own ceiling. A limit landing
    /// on them would throttle a signed-in person for the sake of an attacker who has to sign in
    /// first.
    /// </summary>
    [Theory]
    [InlineData(nameof(PasskeyController.ListCredentials))]
    [InlineData(nameof(PasskeyController.RemoveCredential))]
    [InlineData(nameof(PasskeyController.RegenerateRecoveryCodes))]
    [InlineData(nameof(PasskeyController.GetRecoveryStatus))]
    [InlineData(nameof(PasskeyController.CompleteOnboarding))]
    public void SessionGatedManagement_CarriesNoPolicy(string action)
    {
        PolicyOn(Action(action)).Should().BeNull();
    }

    /// <summary>
    /// The ceiling over the real pipeline. The limiter runs ahead of tenant resolution, so a caller
    /// grinding recovery codes is turned away before the subject lookup and before the audit row a
    /// failure would write.
    /// </summary>
    /// <remarks>
    /// On its own host, because the window is spent by the time this returns and the partition here
    /// is the connection: the test server presents no client address, and the signed header that
    /// would name one is not honoured under
    /// <see cref="AuthenticationTestFactory"/> — its configuration reaches the built host but not
    /// the <c>builder.Configuration</c> the policy table reads the instance key from.
    /// </remarks>
    [Fact]
    public async Task RecoveryVerify_TurnsAwayTheAttemptPastTheCeiling()
    {
        var permitLimit = ServiceRegistrationExtensions.ClientAddressPolicies
            .Single(p => p.Policy == "passkey-recovery").PermitLimit;

        await using var factory = new AuthenticationTestFactory();
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= permitLimit; attempt++)
        {
            var allowed = await VerifyAsync(client);
            allowed.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
                $"attempt {attempt} is within the ceiling of {permitLimit}");
        }

        var rejected = await VerifyAsync(client);

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    private static Task<HttpResponseMessage> VerifyAsync(HttpClient client) =>
        client.PostAsJsonAsync(
            "/api/auth/passkey/recovery/verify",
            new { username = "test", code = "AAAAA-BBBBB" });

    private static string? PolicyOn(MethodInfo action) =>
        action.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName;

    private static MethodInfo Action(string name) =>
        typeof(PasskeyController).GetMethod(name)
        ?? throw new InvalidOperationException($"No action named {name} on PasskeyController.");

    private static List<MethodInfo> AnonymousCeremonies() =>
        typeof(PasskeyController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpPostAttribute>().Any()
                && m.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .ToList();
}
