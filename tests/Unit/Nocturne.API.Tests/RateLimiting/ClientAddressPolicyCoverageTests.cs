using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Nocturne.API.Extensions;
using Nocturne.API.RateLimiting;
using Xunit;

namespace Nocturne.API.Tests.RateLimiting;

/// <summary>
/// Pins the rate-limit policies applied to controller actions against the registry that resolves
/// its partition through <see cref="ClientRateLimitKey"/>.
/// </summary>
/// <remarks>
/// A policy registered on its own with a partition key of its own would key on the connection —
/// which, for anything a remote function reaches, is the SvelteKit container shared by every user.
/// </remarks>
public class ClientAddressPolicyCoverageTests
{
    [Fact]
    public void EveryPolicyOnAnAction_PartitionsOnTheClientOrTheTenantHost()
    {
        var applied = AppliedPolicies();

        // A scan that discovered nothing would pass while guarding nothing.
        applied.Should().HaveCountGreaterThan(5,
            "the scan should discover the rate-limited actions");

        var registered = ServiceRegistrationExtensions.RateLimitPartitionKeys(
            new ClientRateLimitKey(new ConfigurationBuilder().Build())).Keys;

        applied.Except(registered).Should().BeEmpty(
            "a policy outside the registry keys on the connection, so every user behind the SSR "
            + "server would share its window");
    }

    [Fact]
    public void EveryClientAddressPolicy_IsAppliedSomewhere()
    {
        var applied = AppliedPolicies();

        ClientAddressPolicyNames()
            // The documentation surface is mapped rather than a controller action, so it carries
            // its policy through RequireRateLimiting in Program.cs.
            .Where(policy => policy != ServiceRegistrationExtensions.DocsRateLimitPolicy)
            .Except(applied)
            .Should().BeEmpty("a policy nothing applies is dead configuration");
    }

    private static List<string> ClientAddressPolicyNames() =>
        ServiceRegistrationExtensions.ClientAddressPolicies.Select(p => p.Policy).ToList();

    private static List<string> AppliedPolicies() =>
        typeof(ServiceRegistrationExtensions).Assembly
            .GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Cast<MemberInfo>()
                .Append(t))
            .SelectMany(m => m.GetCustomAttributes<EnableRateLimitingAttribute>())
            .Select(a => a.PolicyName)
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct()
            .ToList();
}
