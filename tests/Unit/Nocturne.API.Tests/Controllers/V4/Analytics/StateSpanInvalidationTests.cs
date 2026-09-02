using System.Reflection;
using FluentAssertions;
using Nocturne.API.Controllers.V4.Analytics;
using OpenApi.Remote.Attributes;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Analytics;

/// <summary>
/// The category sub-routes serve the same rows as the generic list through remote queries of their
/// own, so a state-span write has to refresh them too — a hint that lists only the generic list
/// leaves a just-set override or exclusion window invisible on every category page.
/// </summary>
[Trait("Category", "Unit")]
public class StateSpanInvalidationTests
{
    private static readonly string[] CategoryReads =
    [
        nameof(StateSpansController.GetPumpModes),
        nameof(StateSpansController.GetConnectivity),
        nameof(StateSpansController.GetOverrides),
        nameof(StateSpansController.GetTemporaryTargets),
        nameof(StateSpansController.GetProfiles),
        nameof(StateSpansController.GetExercise),
        nameof(StateSpansController.GetIllness),
        nameof(StateSpansController.GetTravel),
    ];

    [Theory]
    [InlineData(nameof(StateSpansController.CreateStateSpan))]
    [InlineData(nameof(StateSpansController.UpdateStateSpan))]
    [InlineData(nameof(StateSpansController.DeleteStateSpan))]
    public void EveryWrite_RefreshesTheGenericListAndEveryCategoryRead(string write)
    {
        var invalidates = typeof(StateSpansController).GetMethod(write)!
            .GetCustomAttribute<RemoteCommandAttribute>()!.Invalidates;

        invalidates.Should().Contain(nameof(StateSpansController.GetStateSpans));
        invalidates.Should().Contain(CategoryReads);
    }

    [Theory]
    [InlineData(nameof(StateSpansController.GetPumpModes))]
    [InlineData(nameof(StateSpansController.GetConnectivity))]
    [InlineData(nameof(StateSpansController.GetOverrides))]
    [InlineData(nameof(StateSpansController.GetTemporaryTargets))]
    [InlineData(nameof(StateSpansController.GetProfiles))]
    [InlineData(nameof(StateSpansController.GetExercise))]
    [InlineData(nameof(StateSpansController.GetIllness))]
    [InlineData(nameof(StateSpansController.GetTravel))]
    public void EveryCategoryRead_IsAQueryTheWritesCanRefresh(string read)
    {
        typeof(StateSpansController).GetMethod(read)!
            .GetCustomAttribute<RemoteQueryAttribute>().Should().NotBeNull();
    }
}
