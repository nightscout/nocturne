using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Nocturne.API.Controllers.V4.Monitoring;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// Pins that the tracker read actions are not <c>[AllowAnonymous]</c>, so the fallback
/// authorization policy gates them and a private tenant exposes no tracker to a bare
/// unauthenticated caller (whose permission trie is empty).
/// </summary>
[Trait("Category", "Unit")]
public class TrackersControllerSecurityHardeningTests
{
    /// <summary>
    /// The read actions must not carry <c>[AllowAnonymous]</c>. <c>[AllowAnonymous]</c> skips the
    /// fallback policy, which is the only thing that rejects a bare unauthenticated request on a
    /// private tenant (empty permission trie). Re-adding it reopens the anonymous read hole, so
    /// this test fails.
    /// </summary>
    [Theory]
    [InlineData(nameof(TrackersController.GetDefinitions))]
    [InlineData(nameof(TrackersController.GetDefinition))]
    [InlineData(nameof(TrackersController.GetActiveInstances))]
    [InlineData(nameof(TrackersController.GetUpcomingInstances))]
    [InlineData(nameof(TrackersController.GetInstanceHistory))]
    public void ReadAction_IsNotAllowAnonymous(string actionName)
    {
        var method = typeof(TrackersController).GetMethod(
            actionName,
            BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"{actionName} must exist on the controller");

        method!.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Should().BeEmpty(
            $"{actionName} must be gated by the fallback policy, not [AllowAnonymous]");
        typeof(TrackersController).GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>()
            .Should().BeEmpty("the controller must not be [AllowAnonymous]");
    }
}
